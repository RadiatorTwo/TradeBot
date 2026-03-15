using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

// ── Interface für Broker-Anbindung ─────────────────────────────────────
// Kann gegen echte IB-Implementierung ausgetauscht werden.

public interface IBrokerService
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task<decimal> GetCurrentPriceAsync(string symbol, CancellationToken ct = default);
    /// <summary>Bid/Ask für Forex/CFD. (0,0) wenn nicht verfügbar.</summary>
    Task<(decimal Bid, decimal Ask)> GetBidAskAsync(string symbol, CancellationToken ct = default);
    Task<List<decimal>> GetRecentPricesAsync(string symbol, int count = 20, CancellationToken ct = default);
    /// <summary>Historische Candles (Close-Preise) für einen Zeitrahmen (z. B. "1D", "4H", "1H").</summary>
    Task<List<decimal>> GetPriceHistoryAsync(string symbol, string resolution, int count, CancellationToken ct = default);
    /// <summary>Historische OHLC-Candles fuer technische Indikatoren (ATR etc.).</summary>
    Task<List<OhlcCandle>> GetCandlesAsync(string symbol, string resolution, int count, CancellationToken ct = default);
    Task<decimal> GetAccountCashAsync(CancellationToken ct = default);
    Task<decimal> GetPortfolioValueAsync(CancellationToken ct = default);
    /// <summary>Konto-Details (Balance, Equity, Margin, FreeMargin).</summary>
    Task<AccountDetails> GetAccountDetailsAsync(CancellationToken ct = default);
    Task<List<Position>> GetPositionsAsync(CancellationToken ct = default);
    /// <summary>Order platzieren (Forex/CFD: quantity in Lots). Gibt Order-/Position-IDs für DB-Mapping zurück.</summary>
    Task<PlaceOrderResult> PlaceOrderAsync(string symbol, TradeAction action, decimal quantityLots, decimal? stopLoss, decimal? takeProfit, CancellationToken ct = default);
    /// <summary>Position schließen (komplett: quantity=null oder 0; teilweise: quantity = Lots).</summary>
    Task<bool> ClosePositionAsync(string positionIdOrSymbol, decimal? quantity, CancellationToken ct = default);

    /// <summary>Geschlossene Positionen der letzten N Tage vom Broker abrufen.</summary>
    Task<List<BrokerClosedPosition>> GetClosedPositionsAsync(int lookbackDays = 1, CancellationToken ct = default);

    /// <summary>Stop-Loss einer Position beim Broker aktualisieren (Trailing/Breakeven).</summary>
    Task<bool> UpdatePositionStopLossAsync(string positionId, decimal newStopLoss, CancellationToken ct = default);

    /// <summary>Historische Candles fuer einen Datumsbereich laden (fuer Backtesting).</summary>
    Task<List<OhlcCandle>> GetHistoricalCandlesAsync(string symbol, string resolution, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>Pending Orders (Limit/Stop) vom Broker abrufen. Keine SL/TP einer Position.</summary>
    Task<List<BrokerPendingOrder>> GetPendingOrdersAsync(CancellationToken ct = default);

    /// <summary>Eine Pending Order beim Broker stornieren.</summary>
    Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default);
}

// ── Simulierte Implementierung für Paper Trading / Entwicklung ─────────
// HINWEIS: Ersetze diese Klasse durch eine echte IB-Gateway-Anbindung
// z.B. mit dem IBApi NuGet-Paket oder der InterReact-Bibliothek.

public class SimulatedBrokerService : IBrokerService
{
    private readonly IBSettings _settings;
    private readonly ILogger<SimulatedBrokerService> _logger;
    private bool _connected;
    private decimal _cash = 100_000m;
    private readonly Dictionary<string, Position> _positions = new();
    private readonly Random _rng = new();

    // Simulierte Basispreise
    private readonly Dictionary<string, decimal> _basePrices = new()
    {
        ["AAPL"] = 185.50m, ["MSFT"] = 425.30m, ["GOOGL"] = 175.20m,
        ["AMZN"] = 195.80m, ["NVDA"] = 880.50m, ["META"] = 520.40m,
        ["TSLA"] = 245.60m, ["JPM"] = 205.30m,  ["V"] = 285.10m,
        ["SPY"] = 525.70m
    };

    public bool IsConnected => _connected;

    public SimulatedBrokerService(IOptions<IBSettings> settings, ILogger<SimulatedBrokerService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Connecting to IB Gateway at {Host}:{Port} (Paper: {Paper})",
            _settings.Host, _settings.Port, _settings.UsePaperTrading);

        _connected = true;
        _logger.LogInformation("Simulated broker connection established");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        _logger.LogInformation("Broker disconnected");
        return Task.CompletedTask;
    }

    public Task<decimal> GetCurrentPriceAsync(string symbol, CancellationToken ct = default)
    {
        var basePrice = _basePrices.GetValueOrDefault(symbol, 100m);
        // Simuliere kleine Preisschwankungen (±2%)
        var variation = (decimal)(_rng.NextDouble() * 0.04 - 0.02);
        var price = Math.Round(basePrice * (1 + variation), 2);
        return Task.FromResult(price);
    }

    public Task<(decimal Bid, decimal Ask)> GetBidAskAsync(string symbol, CancellationToken ct = default)
    {
        var price = _basePrices.GetValueOrDefault(symbol, 100m);
        var spread = (decimal)(_rng.NextDouble() * 0.002);
        var bid = Math.Round(price * (1 - spread / 2), 4);
        var ask = Math.Round(price * (1 + spread / 2), 4);
        return Task.FromResult((bid, ask));
    }

    public async Task<List<decimal>> GetRecentPricesAsync(string symbol, int count = 20, CancellationToken ct = default)
    {
        return await GetPriceHistoryAsync(symbol, "1H", count, ct);
    }

    public async Task<List<decimal>> GetPriceHistoryAsync(string symbol, string resolution, int count, CancellationToken ct = default)
    {
        var candles = await GetCandlesAsync(symbol, resolution, count, ct);
        return candles.Select(c => c.Close).ToList();
    }

    public async Task<List<OhlcCandle>> GetCandlesAsync(string symbol, string resolution, int count, CancellationToken ct = default)
    {
        var candles = new List<OhlcCandle>();
        var currentPrice = await GetCurrentPriceAsync(symbol, ct);

        for (int i = 0; i < count; i++)
        {
            var variation = (decimal)(_rng.NextDouble() * 0.06 - 0.03);
            var close = Math.Round(currentPrice * (1 + variation), 4);
            var spread = Math.Abs(close * (decimal)(_rng.NextDouble() * 0.01));
            candles.Add(new OhlcCandle
            {
                Open = Math.Round(close + (decimal)(_rng.NextDouble() * 0.005 - 0.0025) * currentPrice, 4),
                High = Math.Round(close + spread, 4),
                Low = Math.Round(close - spread, 4),
                Close = close,
                Time = DateTimeOffset.UtcNow.AddHours(-count + i).ToUnixTimeMilliseconds()
            });
        }
        return candles;
    }

    public Task<decimal> GetAccountCashAsync(CancellationToken ct = default)
        => Task.FromResult(_cash);

    public Task<decimal> GetPortfolioValueAsync(CancellationToken ct = default)
    {
        var positionValue = _positions.Values.Sum(p => p.CurrentPrice * p.Quantity);
        return Task.FromResult(_cash + positionValue);
    }

    public async Task<AccountDetails> GetAccountDetailsAsync(CancellationToken ct = default)
    {
        var equity = await GetPortfolioValueAsync(ct);
        return new AccountDetails
        {
            Balance = _cash,
            Equity = equity,
            Margin = 0,
            FreeMargin = equity
        };
    }

    public Task<List<Position>> GetPositionsAsync(CancellationToken ct = default)
        => Task.FromResult(_positions.Values.ToList());

    public async Task<PlaceOrderResult> PlaceOrderAsync(string symbol, TradeAction action, decimal quantityLots, decimal? stopLoss, decimal? takeProfit, CancellationToken ct = default)
    {
        if (!_connected)
        {
            _logger.LogWarning("Cannot place order – broker not connected");
            return new PlaceOrderResult { Success = false };
        }

        var quantity = (int)Math.Round(quantityLots);
        if (quantity <= 0) quantity = 1;
        var price = await GetCurrentPriceAsync(symbol, ct);

        if (action == TradeAction.Buy)
        {
            var cost = price * quantity;
            if (cost > _cash)
            {
                _logger.LogWarning("Insufficient cash for {Qty}x {Symbol} @ {Price}", quantity, symbol, price);
                return new PlaceOrderResult { Success = false };
            }

            _cash -= cost;

            if (_positions.TryGetValue(symbol, out var existing))
            {
                var totalQty = existing.Quantity + quantity;
                existing.AveragePrice = (existing.AveragePrice * existing.Quantity + price * quantity) / totalQty;
                existing.Quantity = totalQty;
                existing.CurrentPrice = price;
                existing.LastUpdated = DateTime.UtcNow;
            }
            else
            {
                _positions[symbol] = new Position
                {
                    Symbol = symbol,
                    Quantity = quantity,
                    AveragePrice = price,
                    CurrentPrice = price,
                    LastUpdated = DateTime.UtcNow
                };
            }

            _logger.LogInformation("BUY {Qty} Lots {Symbol} @ ${Price:F2} (SL={SL}, TP={TP})", quantityLots, symbol, price, stopLoss, takeProfit);
        }
        else if (action == TradeAction.Sell)
        {
            if (!_positions.TryGetValue(symbol, out var pos) || pos.Quantity < quantity)
            {
                _logger.LogWarning("Cannot sell {Qty}x {Symbol} – insufficient position", quantity, symbol);
                return new PlaceOrderResult { Success = false };
            }

            _cash += price * quantity;
            pos.Quantity -= quantity;
            pos.CurrentPrice = price;
            pos.LastUpdated = DateTime.UtcNow;

            if (pos.Quantity == 0)
                _positions.Remove(symbol);

            _logger.LogInformation("SELL {Qty} Lots {Symbol} @ ${Price:F2}", quantityLots, symbol, price);
        }

        return new PlaceOrderResult { Success = true };
    }

    public async Task<bool> ClosePositionAsync(string positionIdOrSymbol, decimal? quantity, CancellationToken ct = default)
    {
        if (!_connected)
        {
            _logger.LogWarning("Cannot close position – broker not connected");
            return false;
        }
        // Simulierter Broker: positionIdOrSymbol als Symbol verwenden
        if (!_positions.TryGetValue(positionIdOrSymbol, out var pos))
        {
            _logger.LogWarning("Position not found for close: {Id}", positionIdOrSymbol);
            return false;
        }
        var closeQty = quantity ?? pos.Quantity;
        if (closeQty <= 0) closeQty = pos.Quantity;
        return (await PlaceOrderAsync(positionIdOrSymbol, TradeAction.Sell, closeQty, null, null, ct)).Success;
    }

    public Task<List<BrokerClosedPosition>> GetClosedPositionsAsync(int lookbackDays = 1, CancellationToken ct = default)
        => Task.FromResult(new List<BrokerClosedPosition>());

    public Task<bool> UpdatePositionStopLossAsync(string positionId, decimal newStopLoss, CancellationToken ct = default)
        => Task.FromResult(true);

    public async Task<List<OhlcCandle>> GetHistoricalCandlesAsync(string symbol, string resolution, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var hours = (to - from).TotalHours;
        var count = resolution switch
        {
            "1D" => (int)(hours / 24),
            "4H" => (int)(hours / 4),
            _ => (int)hours
        };
        return await GetCandlesAsync(symbol, resolution, Math.Max(count, 1), ct);
    }

    public Task<List<BrokerPendingOrder>> GetPendingOrdersAsync(CancellationToken ct = default)
        => Task.FromResult(new List<BrokerPendingOrder>());

    public Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default)
        => Task.FromResult(true);
}

// ── Echte IB Gateway Implementierung (Platzhalter) ────────────────────
// TODO: Implementiere dies mit dem offiziellen IBApi NuGet-Paket
// oder der InterReact-Bibliothek (https://github.com/dshe/InterReact)
//
// public class InteractiveBrokersService : IBrokerService
// {
//     // Nutze IBApi.EClientSocket für die Verbindung zu TWS/Gateway
//     // Implementiere EWrapper für Callbacks
//     // Siehe: https://interactivebrokers.github.io/tws-api/
// }
