using System.Collections.Concurrent;
using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

/// <summary>
/// Decorator um IBrokerService fuer Paper-Trading-Modus.
/// Read-Only-Methoden werden an den echten Broker delegiert.
/// Write-Methoden (PlaceOrder, ClosePosition, UpdateSL) simulieren lokal.
/// </summary>
public class PaperTradingBrokerDecorator : IBrokerService
{
    private readonly IBrokerService _inner;
    private readonly IOptionsMonitor<PaperTradingSettings> _settings;
    private readonly ILogger<PaperTradingBrokerDecorator> _logger;

    // Paper-Trading State
    private readonly ConcurrentDictionary<string, PaperPosition> _paperPositions = new();
    private readonly object _balanceLock = new();
    private decimal _paperBalance;
    private int _nextPositionId;

    public bool IsConnected => _inner.IsConnected;
    public bool IsPaperTradingActive => _settings.CurrentValue.Enabled;

    public PaperTradingBrokerDecorator(
        IBrokerService inner,
        IOptionsMonitor<PaperTradingSettings> settings,
        ILogger<PaperTradingBrokerDecorator> logger)
    {
        _inner = inner;
        _settings = settings;
        _logger = logger;
        _paperBalance = settings.CurrentValue.InitialBalance;
    }

    public Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
    public Task DisconnectAsync() => _inner.DisconnectAsync();

    public Task<decimal> GetCurrentPriceAsync(string symbol, CancellationToken ct = default)
        => _inner.GetCurrentPriceAsync(symbol, ct);

    public Task<(decimal Bid, decimal Ask)> GetBidAskAsync(string symbol, CancellationToken ct = default)
        => _inner.GetBidAskAsync(symbol, ct);

    public Task<List<decimal>> GetRecentPricesAsync(string symbol, int count = 20, CancellationToken ct = default)
        => _inner.GetRecentPricesAsync(symbol, count, ct);

    public Task<List<decimal>> GetPriceHistoryAsync(string symbol, string resolution, int count, CancellationToken ct = default)
        => _inner.GetPriceHistoryAsync(symbol, resolution, count, ct);

    public Task<List<OhlcCandle>> GetCandlesAsync(string symbol, string resolution, int count, CancellationToken ct = default)
        => _inner.GetCandlesAsync(symbol, resolution, count, ct);

    public Task<List<OhlcCandle>> GetHistoricalCandlesAsync(string symbol, string resolution, DateTime from, DateTime to, CancellationToken ct = default)
        => _inner.GetHistoricalCandlesAsync(symbol, resolution, from, to, ct);

    public Task<List<BrokerClosedPosition>> GetClosedPositionsAsync(int lookbackDays = 1, CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return _inner.GetClosedPositionsAsync(lookbackDays, ct);
        return Task.FromResult(new List<BrokerClosedPosition>());
    }

    public async Task<decimal> GetAccountCashAsync(CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return await _inner.GetAccountCashAsync(ct);
        lock (_balanceLock) { return _paperBalance; }
    }

    public async Task<decimal> GetPortfolioValueAsync(CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return await _inner.GetPortfolioValueAsync(ct);

        decimal balance;
        lock (_balanceLock) { balance = _paperBalance; }

        var unrealizedPnL = 0m;
        foreach (var pp in _paperPositions.Values.ToList()) // Snapshot
        {
            var price = await _inner.GetCurrentPriceAsync(pp.Symbol, ct);
            var direction = pp.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
            unrealizedPnL += (price - pp.EntryPrice) * pp.Quantity * direction;
        }
        return balance + unrealizedPnL;
    }

    public async Task<AccountDetails> GetAccountDetailsAsync(CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return await _inner.GetAccountDetailsAsync(ct);

        var equity = await GetPortfolioValueAsync(ct);
        return new AccountDetails
        {
            Balance = _paperBalance,
            Equity = equity,
            Margin = 0,
            FreeMargin = equity
        };
    }

    public async Task<List<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return await _inner.GetPositionsAsync(ct);

        var positions = new List<Position>();
        foreach (var pp in _paperPositions.Values.ToList()) // Snapshot
        {
            var currentPrice = await _inner.GetCurrentPriceAsync(pp.Symbol, ct);
            positions.Add(new Position
            {
                Symbol = pp.Symbol,
                Side = pp.Side,
                Quantity = pp.Quantity,
                BrokerPositionId = pp.PositionId,
                AveragePrice = pp.EntryPrice,
                CurrentPrice = currentPrice,
                LastUpdated = DateTime.UtcNow
            });
        }
        return positions;
    }

    // ── Pending Orders (Limit/Stop) fuer Paper-Trading ────────────────
    private readonly ConcurrentDictionary<string, PaperPendingOrderEntry> _paperPendingOrders = new();
    private int _nextOrderId;

    public async Task<PlaceOrderResult> PlaceOrderAsync(
        string symbol, TradeAction action, decimal quantityLots,
        decimal? stopLoss, decimal? takeProfit,
        OrderType orderType = OrderType.Market, decimal? entryPrice = null,
        CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return await _inner.PlaceOrderAsync(symbol, action, quantityLots, stopLoss, takeProfit, orderType, entryPrice, ct);

        var (bid, ask) = await _inner.GetBidAskAsync(symbol, ct);
        var price = (bid > 0 && ask > 0)
            ? (action == TradeAction.Buy ? ask : bid)
            : await _inner.GetCurrentPriceAsync(symbol, ct);
        if (price <= 0)
        {
            _logger.LogWarning("[PAPER] Preis fuer {Symbol} ist 0 – Order abgelehnt", symbol);
            return new PlaceOrderResult { Success = false };
        }

        // Limit/Stop-Orders: als Pending speichern, nicht sofort ausfuehren
        if (orderType != OrderType.Market && entryPrice.HasValue)
        {
            var ordId = $"PAPER-ORD-{Interlocked.Increment(ref _nextOrderId)}";
            _paperPendingOrders[ordId] = new PaperPendingOrderEntry
            {
                OrderId = ordId,
                Symbol = symbol,
                Side = action == TradeAction.Buy ? "buy" : "sell",
                Quantity = quantityLots,
                EntryPrice = entryPrice.Value,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                OrderType = orderType,
                CreatedAt = DateTime.UtcNow
            };

            _logger.LogInformation(
                "[PAPER] Pending {Type} {Side} {Qty:F2} Lots {Symbol} @ {Entry:F5}",
                orderType, action == TradeAction.Buy ? "BUY" : "SELL", quantityLots, symbol, entryPrice.Value);

            return new PlaceOrderResult { Success = true, BrokerOrderId = ordId };
        }

        var posId = $"PAPER-{Interlocked.Increment(ref _nextPositionId)}";
        var side = action == TradeAction.Buy ? "buy" : "sell";

        var pp = new PaperPosition
        {
            PositionId = posId,
            Symbol = symbol,
            Side = side,
            Quantity = quantityLots,
            EntryPrice = price,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            OpenedAt = DateTime.UtcNow
        };

        _paperPositions[posId] = pp;

        _logger.LogInformation(
            "[PAPER] {Action} {Qty:F2} Lots {Symbol} @ {Price:F5} (SL={SL}, TP={TP}, ID={Id})",
            side.ToUpper(), quantityLots, symbol, price, stopLoss, takeProfit, posId);

        return new PlaceOrderResult
        {
            Success = true,
            BrokerOrderId = posId,
            BrokerPositionId = posId
        };
    }

    public async Task<bool> ClosePositionAsync(string positionIdOrSymbol, decimal? quantity, CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return await _inner.ClosePositionAsync(positionIdOrSymbol, quantity, ct);

        // Finde Paper-Position per ID oder Symbol
        PaperPosition? pp = null;
        string? key = null;

        if (_paperPositions.TryGetValue(positionIdOrSymbol, out var byId))
        {
            pp = byId;
            key = positionIdOrSymbol;
        }
        else
        {
            var match = _paperPositions.Values.FirstOrDefault(p =>
                p.Symbol.Equals(positionIdOrSymbol, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                pp = match;
                key = match.PositionId;
            }
        }

        if (pp == null || key == null)
        {
            _logger.LogWarning("[PAPER] Position nicht gefunden: {Id}", positionIdOrSymbol);
            return false;
        }

        var (bid, ask) = await _inner.GetBidAskAsync(pp.Symbol, ct);
        var exitPrice = (bid > 0 && ask > 0)
            ? (pp.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? bid : ask)
            : await _inner.GetCurrentPriceAsync(pp.Symbol, ct);
        var direction = pp.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        var pnl = (exitPrice - pp.EntryPrice) * pp.Quantity * direction;

        lock (_balanceLock) { _paperBalance += pnl; }
        _paperPositions.TryRemove(key, out _);

        _logger.LogInformation(
            "[PAPER] Close {Side} {Qty:F2} Lots {Symbol} @ {Price:F5} (Entry={Entry:F5}, PnL={PnL:+0.00;-0.00})",
            pp.Side.ToUpper(), pp.Quantity, pp.Symbol, exitPrice, pp.EntryPrice, pnl);

        return true;
    }

    public Task<List<BrokerPendingOrder>> GetPendingOrdersAsync(CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return _inner.GetPendingOrdersAsync(ct);

        var list = _paperPendingOrders.Values.Select(o => new BrokerPendingOrder
        {
            OrderId = o.OrderId,
            Symbol = o.Symbol,
            Side = o.Side,
            Qty = o.Quantity,
            Price = o.EntryPrice,
            Type = o.OrderType.ToString().ToLower(),
            CreatedAt = o.CreatedAt
        }).ToList();
        return Task.FromResult(list);
    }

    public Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return _inner.CancelOrderAsync(orderId, ct);

        var removed = _paperPendingOrders.TryRemove(orderId, out _);
        if (removed)
            _logger.LogInformation("[PAPER] Pending Order {OrderId} storniert", orderId);
        return Task.FromResult(removed);
    }

    public async Task<bool> UpdatePositionStopLossAsync(string positionId, decimal newStopLoss, CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return await _inner.UpdatePositionStopLossAsync(positionId, newStopLoss, ct);

        if (_paperPositions.TryGetValue(positionId, out var pp))
        {
            pp.StopLoss = newStopLoss;
            _logger.LogDebug("[PAPER] SL aktualisiert: {Id} → {SL:F5}", positionId, newStopLoss);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Prueft alle Pending Orders und fuellt sie wenn der Preis das Entry-Level erreicht hat.
    /// Wird pro Trading-Zyklus aufgerufen.
    /// </summary>
    public async Task CheckAndFillPendingOrdersAsync(CancellationToken ct)
    {
        if (!_settings.CurrentValue.Enabled || _paperPendingOrders.IsEmpty) return;

        foreach (var (orderId, order) in _paperPendingOrders.ToArray())
        {
            var currentPrice = await _inner.GetCurrentPriceAsync(order.Symbol, ct);
            if (currentPrice <= 0) continue;

            var shouldFill = (order.OrderType, order.Side) switch
            {
                (OrderType.Limit, "buy") => currentPrice <= order.EntryPrice,
                (OrderType.Limit, "sell") => currentPrice >= order.EntryPrice,
                (OrderType.Stop, "buy") => currentPrice >= order.EntryPrice,
                (OrderType.Stop, "sell") => currentPrice <= order.EntryPrice,
                _ => false
            };

            if (!shouldFill) continue;

            // Order ausfuehren: PaperPosition erstellen
            var posId = $"PAPER-{Interlocked.Increment(ref _nextPositionId)}";
            _paperPositions[posId] = new PaperPosition
            {
                PositionId = posId,
                Symbol = order.Symbol,
                Side = order.Side,
                Quantity = order.Quantity,
                EntryPrice = order.EntryPrice,
                StopLoss = order.StopLoss,
                TakeProfit = order.TakeProfit,
                OpenedAt = DateTime.UtcNow
            };

            _paperPendingOrders.TryRemove(orderId, out _);

            _logger.LogInformation(
                "[PAPER] Pending {Type} gefuellt: {Side} {Qty:F2} Lots {Symbol} @ {Price:F5} (Entry={Entry:F5})",
                order.OrderType, order.Side.ToUpper(), order.Quantity, order.Symbol, currentPrice, order.EntryPrice);
        }
    }

    /// <summary>Interne Klasse fuer Pending Orders im Paper-Trading.</summary>
    private class PaperPendingOrderEntry
    {
        public string OrderId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string Side { get; set; } = "buy";
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal? StopLoss { get; set; }
        public decimal? TakeProfit { get; set; }
        public OrderType OrderType { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>Interne Klasse fuer simulierte Positionen.</summary>
    private class PaperPosition
    {
        public string PositionId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string Side { get; set; } = "buy";
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal? StopLoss { get; set; }
        public decimal? TakeProfit { get; set; }
        public DateTime OpenedAt { get; set; }
    }
}
