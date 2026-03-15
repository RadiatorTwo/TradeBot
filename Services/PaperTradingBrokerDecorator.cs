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

    public async Task<PlaceOrderResult> PlaceOrderAsync(
        string symbol, TradeAction action, decimal quantityLots,
        decimal? stopLoss, decimal? takeProfit, CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return await _inner.PlaceOrderAsync(symbol, action, quantityLots, stopLoss, takeProfit, ct);

        var price = await _inner.GetCurrentPriceAsync(symbol, ct);
        if (price <= 0)
        {
            _logger.LogWarning("[PAPER] Preis fuer {Symbol} ist 0 – Order abgelehnt", symbol);
            return new PlaceOrderResult { Success = false };
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

        var currentPrice = await _inner.GetCurrentPriceAsync(pp.Symbol, ct);
        var direction = pp.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        var pnl = (currentPrice - pp.EntryPrice) * pp.Quantity * direction;

        lock (_balanceLock) { _paperBalance += pnl; }
        _paperPositions.TryRemove(key, out _);

        _logger.LogInformation(
            "[PAPER] Close {Side} {Qty:F2} Lots {Symbol} @ {Price:F5} (Entry={Entry:F5}, PnL={PnL:+0.00;-0.00})",
            pp.Side.ToUpper(), pp.Quantity, pp.Symbol, currentPrice, pp.EntryPrice, pnl);

        return true;
    }

    public Task<List<BrokerPendingOrder>> GetPendingOrdersAsync(CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return _inner.GetPendingOrdersAsync(ct);
        return Task.FromResult(new List<BrokerPendingOrder>());
    }

    public Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default)
    {
        if (!_settings.CurrentValue.Enabled)
            return _inner.CancelOrderAsync(orderId, ct);
        return Task.FromResult(true);
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
