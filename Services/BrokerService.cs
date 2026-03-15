using ClaudeTradingBot.Models;

namespace ClaudeTradingBot.Services;

public interface IBrokerService
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task<decimal> GetCurrentPriceAsync(string symbol, CancellationToken ct = default);
    /// <summary>Bid/Ask fuer Forex/CFD. (0,0) wenn nicht verfuegbar.</summary>
    Task<(decimal Bid, decimal Ask)> GetBidAskAsync(string symbol, CancellationToken ct = default);
    Task<List<decimal>> GetRecentPricesAsync(string symbol, int count = 20, CancellationToken ct = default);
    /// <summary>Historische Candles (Close-Preise) fuer einen Zeitrahmen (z. B. "1D", "4H", "1H").</summary>
    Task<List<decimal>> GetPriceHistoryAsync(string symbol, string resolution, int count, CancellationToken ct = default);
    /// <summary>Historische OHLC-Candles fuer technische Indikatoren (ATR etc.).</summary>
    Task<List<OhlcCandle>> GetCandlesAsync(string symbol, string resolution, int count, CancellationToken ct = default);
    Task<decimal> GetAccountCashAsync(CancellationToken ct = default);
    Task<decimal> GetPortfolioValueAsync(CancellationToken ct = default);
    /// <summary>Konto-Details (Balance, Equity, Margin, FreeMargin).</summary>
    Task<AccountDetails> GetAccountDetailsAsync(CancellationToken ct = default);
    Task<List<Position>> GetPositionsAsync(CancellationToken ct = default);
    /// <summary>Order platzieren (Forex/CFD: quantity in Lots). Gibt Order-/Position-IDs fuer DB-Mapping zurueck.</summary>
    Task<PlaceOrderResult> PlaceOrderAsync(string symbol, TradeAction action, decimal quantityLots, decimal? stopLoss, decimal? takeProfit, OrderType orderType = OrderType.Market, decimal? entryPrice = null, CancellationToken ct = default);
    /// <summary>Position schliessen (komplett: quantity=null oder 0; teilweise: quantity = Lots).</summary>
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
