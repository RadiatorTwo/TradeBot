using Prometheus;

namespace ClaudeTradingBot.Services;

/// <summary>
/// Zentraler Prometheus-Metriken-Service fuer den Trading Bot.
/// Alle Metriken werden hier definiert und von anderen Services inkrementiert.
/// Endpunkt: /metrics (Prometheus-Format)
/// </summary>
public static class TradingMetrics
{
    // ── Trade-Metriken ──────────────────────────────────────────────────

    public static readonly Counter TradesTotal = Metrics.CreateCounter(
        "trading_trades_total",
        "Gesamtzahl der Trades nach Status",
        new CounterConfiguration { LabelNames = new[] { "status", "account_id" } });

    public static readonly Counter TradesByAction = Metrics.CreateCounter(
        "trading_trades_by_action_total",
        "Trades nach Aktion (buy/sell/hold)",
        new CounterConfiguration { LabelNames = new[] { "action", "symbol", "account_id" } });

    public static readonly Histogram TradeExecutionDuration = Metrics.CreateHistogram(
        "trading_trade_execution_seconds",
        "Dauer der Trade-Ausfuehrung (Broker-Aufruf)",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.1, 2, 8) });

    // ── LLM-Metriken ────────────────────────────────────────────────────

    public static readonly Histogram LlmAnalysisDuration = Metrics.CreateHistogram(
        "trading_llm_analysis_seconds",
        "Dauer der LLM-Analyse pro Symbol",
        new HistogramConfiguration
        {
            LabelNames = new[] { "provider" },
            Buckets = Histogram.ExponentialBuckets(0.5, 2, 8)
        });

    public static readonly Counter LlmCallsTotal = Metrics.CreateCounter(
        "trading_llm_calls_total",
        "Gesamtzahl der LLM-Aufrufe nach Ergebnis",
        new CounterConfiguration { LabelNames = new[] { "provider", "result" } });

    // ── Broker-API-Metriken ─────────────────────────────────────────────

    public static readonly Histogram BrokerApiDuration = Metrics.CreateHistogram(
        "trading_broker_api_seconds",
        "Dauer der Broker-API-Aufrufe",
        new HistogramConfiguration
        {
            LabelNames = new[] { "operation" },
            Buckets = Histogram.ExponentialBuckets(0.05, 2, 8)
        });

    public static readonly Counter BrokerApiErrors = Metrics.CreateCounter(
        "trading_broker_api_errors_total",
        "Fehler bei Broker-API-Aufrufen",
        new CounterConfiguration { LabelNames = new[] { "operation" } });

    // ── Risk-Management-Metriken ────────────────────────────────────────

    public static readonly Counter RejectedTrades = Metrics.CreateCounter(
        "trading_rejected_total",
        "Abgelehnte Trades nach Grund",
        new CounterConfiguration { LabelNames = new[] { "reason" } });

    public static readonly Gauge KillSwitchActive = Metrics.CreateGauge(
        "trading_kill_switch_active",
        "Kill Switch aktiv (1) oder inaktiv (0)",
        new GaugeConfiguration { LabelNames = new[] { "account_id" } });

    // ── Portfolio-Metriken ───────────────────────────────────────────────

    public static readonly Gauge PortfolioEquity = Metrics.CreateGauge(
        "trading_portfolio_equity",
        "Aktueller Portfolio-Wert (Equity)",
        new GaugeConfiguration { LabelNames = new[] { "account_id" } });

    public static readonly Gauge OpenPositionCount = Metrics.CreateGauge(
        "trading_open_positions",
        "Anzahl offener Positionen",
        new GaugeConfiguration { LabelNames = new[] { "account_id" } });
}
