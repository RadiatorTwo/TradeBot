using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeTradingBot.Models;

public class Trade
{
    [Key]
    public int Id { get; set; }

    /// <summary>Account-ID fuer Multi-Account-Support (Phase 7.1).</summary>
    public string AccountId { get; set; } = "default";

    public string Symbol { get; set; } = string.Empty;
    public TradeAction Action { get; set; }
    public TradeStatus Status { get; set; }

    // Lot-Groesse (z.B. 0.01 fuer 1 Micro-Lot)
    public decimal Quantity { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal Price { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal? ExecutedPrice { get; set; }

    public string ClaudeReasoning { get; set; } = string.Empty;
    public double ClaudeConfidence { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExecutedAt { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Broker-Order-ID (z. B. TradeLocker orderId) fuer Zuordnung.</summary>
    public string? BrokerOrderId { get; set; }

    /// <summary>Broker-Position-ID (z. B. TradeLocker positionId) fuer ClosePosition.</summary>
    public string? BrokerPositionId { get; set; }

    /// <summary>Spread in Pips zum Zeitpunkt der Trade-Eroeffnung.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal? SpreadAtEntry { get; set; }

    /// <summary>Zeitpunkt der Positionsschliessung (SL/TP/manuell).</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>Realisierter Gewinn/Verlust nach Schliessung.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal? RealizedPnL { get; set; }

    /// <summary>Order-Typ: Market (default), Limit, Stop.</summary>
    public OrderType OrderType { get; set; } = OrderType.Market;

    /// <summary>Gewuenschter Einstiegspreis fuer Limit/Stop-Orders.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal? EntryPrice { get; set; }

    /// <summary>LLM-erkannter Setup-Typ (z.B. "EMA-Cross", "Breakout", "RSI-Oversold").</summary>
    public string? SetupType { get; set; }
    /// <summary>Komma-separierte Tags (z.B. "london-session,high-volatility").</summary>
    public string? Tags { get; set; }
    /// <summary>Freitext-Notizen des Benutzers.</summary>
    public string? Notes { get; set; }
}

public class Position
{
    [Key]
    public int Id { get; set; }

    public string Symbol { get; set; } = string.Empty;

    /// <summary>buy oder sell.</summary>
    public string Side { get; set; } = "buy";

    /// <summary>Groesse in Lots (Forex/CFD) oder Stueckzahl.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal Quantity { get; set; }

    /// <summary>Broker-Position-ID fuer ClosePositionAsync (z. B. TradeLocker positionId).</summary>
    public string? BrokerPositionId { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal AveragePrice { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal CurrentPrice { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal UnrealizedPnL
    {
        get
        {
            var direction = Side.Equals("sell", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
            return (CurrentPrice - AveragePrice) * Quantity * direction;
        }
    }

    public double UnrealizedPnLPercent => AveragePrice > 0
        ? (double)((CurrentPrice - AveragePrice) / AveragePrice) * 100.0
            * (Side.Equals("sell", StringComparison.OrdinalIgnoreCase) ? -1 : 1)
        : 0.0;

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class DailyPnL
{
    [Key]
    public int Id { get; set; }

    public string AccountId { get; set; } = "default";

    public DateOnly Date { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal RealizedPnL { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal UnrealizedPnL { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal PortfolioValue { get; set; }

    public int TradeCount { get; set; }

    /// <summary>Hoechster Equity-Wert bis zu diesem Tag (fuer Drawdown-Berechnung).</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal PeakEquity { get; set; }

    /// <summary>Portfolio-Wert zu Tagesbeginn (erster Eintrag des Tages). Referenz fuer Daily-Loss-Limit.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal? StartOfDayEquity { get; set; }
}

public class TradingLog
{
    [Key]
    public int Id { get; set; }

    public string AccountId { get; set; } = "default";

    public string Level { get; set; } = "Info";
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>Per-Account Einstellungen in der DB. JSON-Spalten statt 30+ Einzelspalten.</summary>
public class AccountSettingsEntity
{
    [Key]
    public string AccountId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TradeLockerJson { get; set; } = "{}";
    public string RiskSettingsJson { get; set; } = "{}";
    public string PaperTradingJson { get; set; } = "{}";
    public string WatchListJson { get; set; } = "[]";
    public string StrategyPrompt { get; set; } = string.Empty;
    public string StrategyLabel { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Globale Key-Value-Settings in der DB.</summary>
public class GlobalSettingsEntity
{
    [Key]
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Snapshot des Engine-Zustands bei Shutdown (fuer Graceful Shutdown & Recovery).</summary>
public class EngineStateSnapshot
{
    [Key]
    public int Id { get; set; }
    public string AccountId { get; set; } = "default";
    public bool WasRunning { get; set; }
    public bool WasPaused { get; set; }
    public bool WasKillSwitchActive { get; set; }
    public int OpenPositionCount { get; set; }
    /// <summary>JSON-Array mit offenen Positionen zum Shutdown-Zeitpunkt.</summary>
    public string OpenPositionsJson { get; set; } = "[]";
    public DateTime ShutdownAt { get; set; } = DateTime.UtcNow;
    public bool CleanShutdown { get; set; }
}
