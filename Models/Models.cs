using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace ClaudeTradingBot.Models;

// ── Trade-Entscheidung von Claude ──────────────────────────────────────

public enum TradeAction
{
    Buy,
    Sell,
    Hold
}

public enum TradeStatus
{
    Pending,
    Executed,
    Failed,
    Cancelled,
    Rejected
}

// ── Datenbank-Entitäten ────────────────────────────────────────────────

public class Trade
{
    [Key]
    public int Id { get; set; }
    
    public string Symbol { get; set; } = string.Empty;
    public TradeAction Action { get; set; }
    public TradeStatus Status { get; set; }
    
    // Lot-Größe (z.B. 0.01 für 1 Micro-Lot)
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

    /// <summary>Broker-Order-ID (z. B. TradeLocker orderId) für Zuordnung.</summary>
    public string? BrokerOrderId { get; set; }

    /// <summary>Broker-Position-ID (z. B. TradeLocker positionId) für ClosePosition.</summary>
    public string? BrokerPositionId { get; set; }

    /// <summary>Spread in Pips zum Zeitpunkt der Trade-Eroeffnung.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal? SpreadAtEntry { get; set; }

    /// <summary>Zeitpunkt der Positionsschließung (SL/TP/manuell).</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>Realisierter Gewinn/Verlust nach Schließung.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal? RealizedPnL { get; set; }
}

public class Position
{
    [Key]
    public int Id { get; set; }

    public string Symbol { get; set; } = string.Empty;

    /// <summary>buy oder sell.</summary>
    public string Side { get; set; } = "buy";

    /// <summary>Größe in Lots (Forex/CFD) oder Stückzahl.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal Quantity { get; set; }

    /// <summary>Broker-Position-ID für ClosePositionAsync (z. B. TradeLocker positionId).</summary>
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
}

public class TradingLog
{
    [Key]
    public int Id { get; set; }
    
    public string Level { get; set; } = "Info";
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// ── Claude API DTOs ────────────────────────────────────────────────────

public class ClaudeAnalysisRequest
{
    public string Symbol { get; set; } = string.Empty;
    public decimal CurrentPrice { get; set; }
    /// <summary>Bid-Preis (Forex/CFD). 0 wenn nicht verfügbar.</summary>
    public decimal Bid { get; set; }
    /// <summary>Ask-Preis (Forex/CFD). 0 wenn nicht verfügbar.</summary>
    public decimal Ask { get; set; }
    public decimal DayChange { get; set; }
    public decimal Volume { get; set; }
    public List<decimal> RecentPrices { get; set; } = new();
    /// <summary>Candles 1D (Close-Preise, älteste zuerst).</summary>
    public List<decimal> Candles1D { get; set; } = new();
    /// <summary>Candles 4H (Close-Preise).</summary>
    public List<decimal> Candles4H { get; set; } = new();
    /// <summary>Candles 1H (Close-Preise).</summary>
    public List<decimal> Candles1H { get; set; } = new();
    public Position? CurrentPosition { get; set; }
    public decimal AvailableCash { get; set; }
    public decimal PortfolioValue { get; set; }
    /// <summary>Technische Indikatoren (RSI, EMA, MACD, ATR, Bollinger Bands).</summary>
    public TechnicalIndicators? Indicators { get; set; }

    /// <summary>Letzte geschlossene Trades fuer dieses Symbol (Feedback-Loop fuer das LLM).</summary>
    public List<RecentTradeResult> RecentTradeResults { get; set; } = new();

    /// <summary>Aktuelle News-Headlines fuer dieses Symbol (Sentiment-Kontext fuer das LLM).</summary>
    public List<string> NewsHeadlines { get; set; } = new();
}

/// <summary>Zusammenfassung eines geschlossenen Trades fuer den LLM-Feedback-Loop.</summary>
public record RecentTradeResult
{
    public string Symbol { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public decimal RealizedPnL { get; init; }
    public double Confidence { get; init; }
    public DateTime ClosedAt { get; init; }
}

/// <summary>Berechnete technische Indikatoren fuer ein Symbol.</summary>
public class TechnicalIndicators
{
    // RSI
    public decimal? RSI14 { get; set; }

    // EMA
    public decimal? EMA20 { get; set; }
    public decimal? EMA50 { get; set; }
    public decimal? EMA200 { get; set; }

    // MACD
    public decimal? MACDLine { get; set; }
    public decimal? MACDSignal { get; set; }
    public decimal? MACDHistogram { get; set; }

    // ATR
    public decimal? ATR14 { get; set; }

    // Bollinger Bands
    public decimal? BollingerUpper { get; set; }
    public decimal? BollingerMiddle { get; set; }
    public decimal? BollingerLower { get; set; }
}

public class ClaudeTradeRecommendation
{
    public string Symbol { get; set; } = string.Empty;
    public string Action { get; set; } = "hold";
    // Für Forex/CFD: Lot-Größe (z.B. 0.01)
    public decimal Quantity { get; set; }
    public double Confidence { get; set; }
    public string Reasoning { get; set; } = string.Empty;
    public decimal? StopLossPrice { get; set; }
    public decimal? TakeProfitPrice { get; set; }
}

// ── Konfigurationsklassen ──────────────────────────────────────────────

/// <summary>Wahl des LLM-Providers: Anthropic (Claude), Gemini (kostenloser Cloud-Free-Tier), OpenAICompatible (z. B. Ollama).</summary>
public class LlmSettings
{
    public string Provider { get; set; } = "Gemini"; // "Anthropic" | "Gemini" | "OpenAICompatible"
}

/// <summary>Claude von Anthropic = dieselbe API wie Cursor Agentic Workflow und das Python-Paket anthropic (client.messages.create). API-Key von https://console.anthropic.com/ (Format sk-ant-...).</summary>
public class AnthropicSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-sonnet-4-20250514";
    public int MaxTokens { get; set; } = 2048;
}

/// <summary>Google Gemini API. Free-Tier: Oft muss ein Billing-Account verknüpft werden (wird nicht belastet), sonst Quota 0. Key: https://aistudio.google.com/apikey</summary>
public class GeminiSettings
{
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>z. B. gemini-2.0-flash, gemini-2.0-flash-lite (mehr Free-Quota), gemini-2.5-flash</summary>
    public string Model { get; set; } = "gemini-2.0-flash-lite";
    public int MaxTokens { get; set; } = 2048;
}

/// <summary>Für OpenAI-kompatible Endpoints (Ollama, LM Studio, OpenRouter).</summary>
public class OpenAICompatibleSettings
{
    public string BaseUrl { get; set; } = "http://localhost:11434/v1/"; // Ollama default
    public string ApiKey { get; set; } = string.Empty; // bei Ollama leer
    /// <summary>Ollama: qwen2.5:7b (empfohlen für Trading/JSON), llama3.2:3b (leicht), mistral, deepseek-r1:7b</summary>
    public string Model { get; set; } = "qwen2.5:7b";
    public int MaxTokens { get; set; } = 2048;
    public int TimeoutSeconds { get; set; } = 60;
}

// Legacy-IB-Konfiguration (wird nicht mehr aktiv verwendet,
// bleibt aber für die simulierte Broker-Implementierung erhalten)
public class IBSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4002;
    public int ClientId { get; set; } = 1;
    public bool UsePaperTrading { get; set; } = true;
}

// TradeLocker Konfiguration
public class TradeLockerSettings
{
    public string BaseUrl { get; set; } = "https://demo.tradelocker.com/backend-api";
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string? AccountId { get; set; }
}

public class RiskSettings
{
    public double MinConfidence { get; set; } = 0.65;
    public double MaxPositionSizePercent { get; set; } = 10.0;
    public double MaxDailyLossPercent { get; set; } = 3.0;
    public double StopLossPercent { get; set; } = 5.0;
    public int MaxOpenPositions { get; set; } = 10;
    public int TradingIntervalMinutes { get; set; } = 15;
    public bool KillSwitchEnabled { get; set; } = true;
    public decimal MaxDailyLossAbsolute { get; set; } = 500m;

    // ── Phase 2: Session-Filter ──────────────────────────────────────
    /// <summary>Trading nur waehrend bestimmter Sessions erlauben. Leer = immer aktiv.</summary>
    public List<string> AllowedSessions { get; set; } = new();

    // ── Phase 1: Gewinnschutz ────────────────────────────────────────
    /// <summary>Trailing Stop in Pips. 0 = deaktiviert. Zieht SL nach wenn Gewinn > Distanz.</summary>
    public double TrailingStopPips { get; set; } = 0;
    /// <summary>Breakeven: SL auf Einstiegspreis verschieben wenn Gewinn >= X Pips. 0 = deaktiviert.</summary>
    public double BreakevenTriggerPips { get; set; } = 0;
    /// <summary>Risiko pro Trade in % des Portfolios. Position Sizing basierend auf SL-Distanz. 0 = deaktiviert (nutzt MaxPositionSizePercent).</summary>
    public double RiskPerTradePercent { get; set; } = 0;

    // ── Phase 4: Robustheit ───────────────────────────────────────────
    /// <summary>Max. Drawdown vom Equity-Peak in %. Kill Switch bei Ueberschreitung. 0 = deaktiviert.</summary>
    public double MaxDrawdownPercent { get; set; } = 0;
    /// <summary>Max. Wochenverlust in %. Neue Trades werden blockiert bei Ueberschreitung. 0 = deaktiviert.</summary>
    public double MaxWeeklyLossPercent { get; set; } = 0;
    /// <summary>Max. Monatsverlust in %. Neue Trades werden blockiert bei Ueberschreitung. 0 = deaktiviert.</summary>
    public double MaxMonthlyLossPercent { get; set; } = 0;
    /// <summary>Max. korrelierte Exposure in % des Portfolios. 0 = deaktiviert.</summary>
    public double MaxCorrelatedExposurePercent { get; set; } = 0;

    // ── Phase 8.1: Spread-Filter ──────────────────────────────────────
    /// <summary>Max. erlaubter Spread in Pips. Trade wird abgelehnt wenn Spread hoeher. 0 = deaktiviert.</summary>
    public double MaxSpreadPips { get; set; } = 0;
}

/// <summary>Statische Korrelationsmatrix fuer gaengige Forex/CFD-Pairs.</summary>
public static class CorrelationMatrix
{
    /// <summary>Korrelationskoeffizienten zwischen Instrumenten (-1.0 bis 1.0).</summary>
    private static readonly Dictionary<(string, string), double> _correlations = new()
    {
        // Stark korrelierte Paare
        { ("EURUSD", "GBPUSD"), 0.85 },
        { ("EURUSD", "AUDUSD"), 0.70 },
        { ("EURUSD", "NZDUSD"), 0.65 },
        { ("GBPUSD", "AUDUSD"), 0.60 },
        { ("GBPUSD", "NZDUSD"), 0.55 },
        { ("AUDUSD", "NZDUSD"), 0.90 },

        // Invers korrelierte Paare (USD auf der anderen Seite)
        { ("EURUSD", "USDCHF"), -0.90 },
        { ("EURUSD", "USDJPY"), -0.50 },
        { ("GBPUSD", "USDCHF"), -0.80 },
        { ("GBPUSD", "USDJPY"), -0.40 },

        // Gold-Korrelationen
        { ("XAUUSD", "EURUSD"), 0.40 },
        { ("XAUUSD", "USDJPY"), -0.30 },
        { ("XAUUSD", "USDCHF"), -0.35 },

        // Indizes
        { ("US100", "US500"), 0.95 },
        { ("US100", "US30"), 0.85 },
        { ("US500", "US30"), 0.90 },
    };

    /// <summary>Korrelation zwischen zwei Symbolen. 0 wenn unbekannt.</summary>
    public static double GetCorrelation(string symbol1, string symbol2)
    {
        var s1 = symbol1.ToUpperInvariant();
        var s2 = symbol2.ToUpperInvariant();

        if (s1 == s2) return 1.0;

        if (_correlations.TryGetValue((s1, s2), out var corr))
            return corr;
        if (_correlations.TryGetValue((s2, s1), out var corrReverse))
            return corrReverse;

        return 0.0;
    }
}

/// <summary>Pip-Berechnungen fuer Forex/CFD-Instrumente.</summary>
public static class PipCalculator
{
    /// <summary>Pip-Groesse (kleinste Preiseinheit) je Instrument.</summary>
    public static decimal GetPipSize(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        // JPY-Pairs: 1 Pip = 0.01
        if (s.Contains("JPY"))
            return 0.01m;
        // Gold: 1 Pip = 0.1
        if (s.StartsWith("XAU"))
            return 0.1m;
        // Silber: 1 Pip = 0.01
        if (s.StartsWith("XAG"))
            return 0.01m;
        // Indizes: 1 Pip = 1.0
        if (s.Contains("100") || s.Contains("500") || s.Contains("30") || s.Contains("50") ||
            s.StartsWith("US") || s.StartsWith("UK") || s.StartsWith("DE") || s.StartsWith("JP"))
            return 1.0m;
        // Oel: 1 Pip = 0.01
        if (s.StartsWith("XTI") || s.StartsWith("XBR") || s.Contains("OIL"))
            return 0.01m;
        // Forex Standard: 1 Pip = 0.0001
        return 0.0001m;
    }

    /// <summary>Preisdifferenz in Pips umrechnen.</summary>
    public static decimal PriceToPips(string symbol, decimal priceDiff)
        => Math.Abs(priceDiff) / GetPipSize(symbol);

    /// <summary>Pips in Preisdifferenz umrechnen.</summary>
    public static decimal PipsToPrice(string symbol, decimal pips)
        => pips * GetPipSize(symbol);

    /// <summary>Pip-Wert in USD pro Standard-Lot (1.0 Lot).</summary>
    public static decimal GetPipValuePerLot(string symbol, decimal currentPrice)
    {
        var s = symbol.ToUpperInvariant();
        var pipSize = GetPipSize(symbol);

        // JPY-Pairs: Pip-Wert = 100.000 * 0.01 / Preis = 1000/Preis USD
        if (s.Length >= 6 && s[3..6] == "JPY")
            return 100_000m * pipSize / currentPrice;
        // XXX/USD Pairs (EURUSD, GBPUSD): Pip-Wert = 100.000 * 0.0001 = $10
        if (s.Length >= 6 && s[3..6] == "USD")
            return 100_000m * pipSize;
        // USD/XXX Pairs (USDCHF, USDCAD): Pip-Wert = 100.000 * 0.0001 / Preis
        if (s.Length >= 3 && s[..3] == "USD")
            return 100_000m * pipSize / currentPrice;
        // Gold: 100 oz * 0.1 = $10
        if (s.StartsWith("XAU"))
            return 100m * pipSize;
        // Indizes: 1 Kontrakt * 1.0 = $1
        if (s.Contains("100") || s.StartsWith("US") || s.StartsWith("DE"))
            return 1m * pipSize;
        // Fallback: Standard Forex
        return 100_000m * pipSize;
    }
}

/// <summary>Konto-Details vom Broker (Balance, Equity, Margin).</summary>
public class AccountDetails
{
    public decimal Balance { get; set; }
    public decimal Equity { get; set; }
    public decimal Margin { get; set; }
    public decimal FreeMargin { get; set; }
    /// <summary>Unrealisierter P&L = Equity - Balance.</summary>
    public decimal UnrealizedPnL => Equity - Balance;
    /// <summary>Margin-Nutzung = (Margin / Equity) * 100%. Wie TradeLocker es anzeigt.</summary>
    public double MarginUsagePercent => Equity > 0 ? (double)(Margin / Equity) * 100 : 0;
}

/// <summary>OHLC-Candle fuer technische Indikatoren.</summary>
public class OhlcCandle
{
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Time { get; set; }
}

/// <summary>Ergebnis von IBrokerService.PlaceOrderAsync (OrderId/PositionId für DB-Mapping).</summary>
public class PlaceOrderResult
{
    public bool Success { get; set; }
    public string? BrokerOrderId { get; set; }
    public string? BrokerPositionId { get; set; }
}

/// <summary>Geschlossene Position vom Broker (für Sync).</summary>
public class BrokerClosedPosition
{
    public string PositionId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal PnL { get; set; }
    public DateTime ClosedAt { get; set; }
}

// ── TradeLocker API DTOs ───────────────────────────────────────────────

public class TradeLockerAuthResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>Ein Konto aus GET /auth/jwt/all-accounts. API liefert "id", accNum, accountBalance.</summary>
public class TradeLockerAccountInfo
{
    /// <summary>Eindeutige Kontonummer (z. B. 2005672). API-Feld: id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Index für Header accNum (oft einstellig: 1, 2, …). API kann Zahl oder String liefern.</summary>
    [JsonConverter(typeof(AccNumConverter))]
    [JsonPropertyName("accNum")]
    public string AccNum { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Kontostand aus all-accounts (Fallback, wenn /details 0 liefert).</summary>
    [JsonPropertyName("accountBalance")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal AccountBalance { get; set; }
}

/// <summary>Liest accNum als Zahl oder String aus der TradeLocker-API.</summary>
public class AccNumConverter : System.Text.Json.Serialization.JsonConverter<string>
{
    public override string Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number)
            return reader.TryGetInt32(out var n) ? n.ToString() : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (reader.TokenType == System.Text.Json.JsonTokenType.String)
            return reader.GetString() ?? string.Empty;
        return string.Empty;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, string value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

public class TradeLockerInstrumentInfo
{
    public int Id { get; set; }
    /// <summary>Manche APIs liefern tradableInstrumentId statt Id.</summary>
    public int TradableInstrumentId { get; set; }
    /// <summary>API-Feld "name" (z.B. "EURUSD", "XAUUSD"). TradeLocker nutzt "name" statt "symbol".</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    /// <summary>Fallback: manche Broker liefern "symbol" statt "name".</summary>
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;
    /// <summary>Gibt den besten verfügbaren Symbol-Namen zurück.</summary>
    [JsonIgnore]
    public string ResolvedSymbol => !string.IsNullOrWhiteSpace(Name) ? Name : Symbol;
    /// <summary>Routes aus der API (z.B. [{"id":898485,"type":"TRADE"},{"id":1992737,"type":"INFO"}]).</summary>
    public List<TradeLockerRoute>? Routes { get; set; }
}

public class TradeLockerRoute
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
}

public class TradeLockerQuote
{
    public int TradableInstrumentId { get; set; }
    public decimal Bid { get; set; }
    public decimal Ask { get; set; }
}

public class TradeLockerOrderRequest
{
    public decimal Price { get; set; }
    public decimal Qty { get; set; }
    public int RouteId { get; set; }
    public string Side { get; set; } = "buy";
    public decimal? StopLoss { get; set; }
    public string? StopLossType { get; set; } = "absolute";
    public decimal? TakeProfit { get; set; }
    public string? TakeProfitType { get; set; } = "absolute";
    public decimal TrStopOffset { get; set; }
    public int TradableInstrumentId { get; set; }
    public string Type { get; set; } = "market";
    public string Validity { get; set; } = "IOC";
}

public class TradeLockerOrderResponse
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public class TradeLockerPositionInfo
{
    public string Id { get; set; } = string.Empty;
    public int TradableInstrumentId { get; set; }
    public string Side { get; set; } = "buy";
    public decimal Qty { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal MarketPrice { get; set; }
}

/// <summary>Response von GET /trade/accounts/{id}/details. API kann balance/equity oder accountBalance/accountEquity liefern (evtl. als String).</summary>
public class TradeLockerAccountDetails
{
    [JsonPropertyName("balance")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal Balance { get; set; }

    [JsonPropertyName("equity")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal Equity { get; set; }

    [JsonPropertyName("margin")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal Margin { get; set; }

    [JsonPropertyName("freeMargin")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal FreeMargin { get; set; }

    /// <summary>Alternative API-Felder (z. B. all-accounts verwendet accountBalance).</summary>
    [JsonPropertyName("accountBalance")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal AccountBalance { get; set; }

    [JsonPropertyName("accountEquity")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal AccountEquity { get; set; }
}

/// <summary>Liest decimal als Zahl oder String (z. B. "25005.00") aus der TradeLocker-API.</summary>
public class DecimalOrStringConverter : System.Text.Json.Serialization.JsonConverter<decimal>
{
    public override decimal Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number)
            return reader.GetDecimal();
        if (reader.TokenType == System.Text.Json.JsonTokenType.String && decimal.TryParse(reader.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return 0m;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, decimal value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>Ein Candle aus GET /trade/history (o/h/l/c/t oder open/high/low/close/time)</summary>
public class TradeLockerCandle
{
    [JsonPropertyName("open")]
    public decimal Open { get; set; }
    [JsonPropertyName("high")]
    public decimal High { get; set; }
    [JsonPropertyName("low")]
    public decimal Low { get; set; }
    [JsonPropertyName("close")]
    public decimal Close { get; set; }
    [JsonPropertyName("time")]
    public long Time { get; set; }

    // Kurzform (o/h/l/c/t) der API abbilden auf dieselben Werte
    [JsonPropertyName("o")]
    public decimal O { get => Open; set => Open = value; }
    [JsonPropertyName("h")]
    public decimal H { get => High; set => High = value; }
    [JsonPropertyName("l")]
    public decimal L { get => Low; set => Low = value; }
    [JsonPropertyName("c")]
    public decimal C { get => Close; set => Close = value; }
    [JsonPropertyName("t")]
    public long T { get => Time; set => Time = value; }
}

// ── Phase 6.2: News-Sentiment ────────────────────────────────────────

/// <summary>Konfiguration fuer den News-Sentiment-Service.</summary>
public class NewsSettings
{
    /// <summary>Finnhub API Key (kostenlos: https://finnhub.io/register).</summary>
    public string FinnhubApiKey { get; set; } = string.Empty;
    /// <summary>Maximale Anzahl Headlines pro Symbol im LLM-Prompt.</summary>
    public int MaxHeadlinesPerSymbol { get; set; } = 5;
    /// <summary>Aktualisierungsintervall in Minuten.</summary>
    public int RefreshIntervalMinutes { get; set; } = 60;
    /// <summary>News-Feature aktivieren/deaktivieren.</summary>
    public bool Enabled { get; set; }
}

// ── Phase 5: Paper-Trading ─────────────────────────────────────────────

/// <summary>Konfiguration fuer den Paper-Trading-Modus (simulierter Handel mit echten Marktdaten).</summary>
public class PaperTradingSettings
{
    public bool Enabled { get; set; }
    public decimal InitialBalance { get; set; } = 10000m;
}

// ── Phase 5: Multi-Timeframe ──────────────────────────────────────────

/// <summary>Multi-Timeframe-Bestaetigung: EMA-Trend auf hoeherem Timeframe als Filter.</summary>
public class MultiTimeframeSettings
{
    public bool Enabled { get; set; }
    /// <summary>Hoeherer Timeframe fuer Trend-Bestaetigung (z.B. "4H", "1D").</summary>
    public string HigherTimeframe { get; set; } = "4H";
    /// <summary>EMA-Periode fuer Trend-Erkennung (z.B. 200).</summary>
    public int EmaPeriod { get; set; } = 200;
}

// ── Dashboard View Models ──────────────────────────────────────────────

public record DashboardViewModel
{
    public decimal PortfolioValue { get; set; }
    public decimal DailyPnL { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal AvailableCash { get; set; }
    public AccountDetails Account { get; set; } = new();
    public int OpenPositions { get; set; }
    public int TradesToday { get; set; }
    public bool IsEngineRunning { get; set; }
    public bool IsEnginePaused { get; set; }
    public bool IsKillSwitchActive { get; set; }
    public bool IsTradeLockerConnected { get; set; }
    public bool IsMarketOpen { get; set; }
    public bool IsPaperTrading { get; set; }
    public string MarketStatus { get; set; } = string.Empty;
    public List<Position> Positions { get; set; } = new();
    public List<Trade> RecentTrades { get; set; } = new();
    public List<TradingLog> RecentLogs { get; set; } = new();
    public List<DailyPnL> PnLHistory { get; set; } = new();
    public List<UpcomingEventViewModel> UpcomingEvents { get; set; } = new();
    public TradingStatsViewModel Stats { get; set; } = new();
}

/// <summary>Performance-Kennzahlen fuer das Dashboard.</summary>
public record TradingStatsViewModel
{
    // Allgemeine Trade-Uebersicht (immer verfuegbar)
    public int TotalExecuted { get; init; }
    public int TotalRejected { get; init; }
    public int TotalFailed { get; init; }
    public double AvgConfidence { get; init; }

    // PnL-basierte Stats (nur wenn geschlossene Trades mit PnL vorhanden)
    public bool HasPnLData { get; init; }
    public int ClosedTrades { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }
    public double WinRate { get; init; }
    public decimal AvgWin { get; init; }
    public decimal AvgLoss { get; init; }
    public decimal ProfitFactor { get; init; }
    public decimal TotalProfit { get; init; }
    public decimal TotalLoss { get; init; }
    public decimal NetPnL { get; init; }
    public decimal MaxDrawdown { get; init; }
    public double MaxDrawdownPercent { get; init; }
    public double SharpeRatio { get; init; }
    public double TradesPerDay { get; init; }
}

public class UpcomingEventViewModel
{
    public string Title { get; set; } = string.Empty;
    public DateTime EventTime { get; set; }
    public string Impact { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
}
