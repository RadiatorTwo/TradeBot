# Claude Trading Bot

Ein KI-gestuetzter Trading-Bot mit Blazor Server Dashboard, der LLM-Analyse (Claude, Gemini, Ollama)
mit technischen Indikatoren kombiniert und ueber TradeLocker Forex/CFD handelt.

## Architektur

```
┌──────────────────────────────────────────────────────────────┐
│          Blazor Server Dashboard + SignalR (Echtzeit)        │
│  Dashboard · Trades · Backtest · Einstellungen               │
├──────────────────────────────────────────────────────────────┤
│              TradingEngine (BackgroundService)               │
│  ┌────────────┐ ┌────────────┐ ┌──────────────────────────┐  │
│  │ LLM        │ │ TradeLocker│ │ RiskManager              │  │
│  │ (Claude/   │ │ Broker     │ │ · Trailing/Breakeven SL  │  │
│  │  Gemini/   │ │ Service    │ │ · Drawdown-Tracking      │  │
│  │  Ollama)   │ │ + Paper    │ │ · Korrelationscheck      │  │
│  │            │ │   Trading  │ │ · Weekly/Monthly Limits  │  │
│  │            │ │   Decorator│ │ · Kill Switch            │  │
│  └────────────┘ └────────────┘ └──────────────────────────┘  │
│  ┌────────────┐ ┌────────────┐ ┌──────────────────────────┐  │
│  │ Technische │ │ Session-   │ │ Multi-Timeframe-Filter   │  │
│  │ Analyse    │ │ Filter &   │ │ (EMA200 Trend)           │  │
│  │ RSI/EMA/   │ │ Markt-     │ │                          │  │
│  │ MACD/ATR/  │ │ Oeffnungs- │ │ Auto-Reconnect           │  │
│  │ Bollinger  │ │ zeiten     │ │ (Exp. Backoff)           │  │
│  └────────────┘ └────────────┘ └──────────────────────────┘  │
├──────────────────────────────────────────────────────────────┤
│  Backtesting Engine · Telegram Notifications · CSV-Export    │
├──────────────────────────────────────────────────────────────┤
│              SQLite (Trades, PnL, Positionen, Logs)          │
└──────────────────────────────────────────────────────────────┘
```

## Features

### Trading & Analyse
- **LLM-Analyse**: Claude (Anthropic), Gemini (Google), oder lokale Modelle via Ollama
- **Technische Indikatoren**: RSI(14), EMA(20/50/200), MACD, ATR(14), Bollinger Bands
- **Multi-Timeframe-Filter**: EMA200 auf hoeherem Timeframe blockiert Counter-Trend-Trades
- **Session-Filter**: Handel nur waehrend konfigurierbarer Trading-Sessions
- **Markt-Oeffnungszeiten**: Automatische Erkennung von Forex-/Index-Handelszeiten
- **News-Filter**: High-Impact-Events ueberspringen (Economic Calendar)

### Risiko-Management
- Trailing Stop-Loss und Breakeven-Stop
- Risiko-basierte Position Sizing (% des Portfolios pro Trade)
- Drawdown-Tracking mit Kill Switch
- Korrelationscheck (korrelierte Pairs nicht uebergewichten)
- Tages-/Wochen-/Monatsverlust-Limits

### Paper-Trading
- Simulierter Handel mit echten Marktdaten
- Decorator-Pattern: ein Toggle schaltet zwischen Live und Paper um
- Eigenes Startkapital konfigurierbar
- Dashboard zeigt klar "Paper-Trading Aktiv" an

### Backtesting
- Historische Candles von TradeLocker laden
- Strategien: EMA-Cross (EMA20/50) und RSI-Reversal
- Risk-basierte Position Sizing, SL/TP intra-Candle
- Statistiken: Win-Rate, Profit Factor, Max Drawdown, Sharpe Ratio
- Konfigurierbar: Symbol, Timeframe, Zeitraum, SL/TP-Pips

### Dashboard & Monitoring
- Echtzeit-Dashboard via Blazor Server + SignalR
- Equity-Kurve (Chart.js)
- Trade-Statistiken und Performance-Kennzahlen
- Telegram-Notifications bei Trades und Alerts
- CSV-Export der Trade-Historie

### Robustheit
- Auto-Reconnect bei TradeLocker-Verbindungsverlust (exponentielles Backoff)
- Token-Refresh fuer TradeLocker-Sessions
- Position-Sync zwischen Broker und Datenbank

## Setup

1. **.NET 10 SDK** installieren
2. `appsettings.json` konfigurieren:
   - **TradeLocker**: Email, Password, Server (z.B. Demo-Account)
   - **LLM-Provider**: Anthropic API-Key, Gemini API-Key, oder Ollama URL
   - **Telegram** (optional): BotToken und ChatId
3. Starten:
   ```bash
   dotnet run
   ```
4. Dashboard oeffnen: `http://localhost:5000`

## Konfiguration (appsettings.json)

| Sektion | Beschreibung |
|---------|-------------|
| `Llm.Provider` | `"Anthropic"`, `"Gemini"`, oder `"OpenAICompatible"` (Ollama) |
| `TradeLocker` | Email, Password, Server, BaseUrl |
| `RiskManagement` | MinConfidence, MaxPositionSize, StopLoss, Trailing, Breakeven, Drawdown-Limits |
| `PaperTrading` | `Enabled`, `InitialBalance` |
| `MultiTimeframe` | `Enabled`, `HigherTimeframe` (4H/1D), `EmaPeriod` (200) |
| `TradingStrategy` | WatchList (Symbole), AnalysisPromptTemplate |
| `Telegram` | BotToken, ChatId, NotifyOnTrade, NotifyOnAlert |

Alle Risiko- und Trading-Parameter sind auch ueber die Settings-Seite im Dashboard aenderbar.

## Projektstruktur

```
Services/
  TradingEngine.cs              – Haupt-Trading-Zyklus (BackgroundService)
  TradeLockerService.cs         – TradeLocker API-Anbindung + Auto-Reconnect
  PaperTradingBrokerDecorator.cs – Paper-Trading Decorator um IBrokerService
  BacktestEngine.cs             – Backtesting mit EMA-Cross/RSI-Reversal
  RiskManager.cs                – Risiko-Checks, SL-Management, Kill Switch
  TechnicalAnalysisService.cs   – RSI, EMA, MACD, ATR, Bollinger Bands
  ClaudeService.cs              – Anthropic Claude API
  GeminiClaudeService.cs        – Google Gemini API
  OpenAICompatibleClaudeService.cs – Ollama/LM Studio API
  MarketHoursService.cs         – Forex/Index-Oeffnungszeiten
  TradingSessionService.cs      – Session-Filter (London, NY, Tokyo)
  EconomicCalendarService.cs    – High-Impact-Event-Filter
  NotificationService.cs        – Telegram-Benachrichtigungen
  DashboardBroadcastService.cs  – SignalR Dashboard-Push
  PositionSyncService.cs        – Broker-DB Synchronisation
Models/
  Models.cs                     – Entities, DTOs, Konfigurationsklassen
  BacktestModels.cs             – Backtest Config, Result, Stats
Components/Pages/
  Dashboard.razor               – Haupt-Dashboard mit Echtzeit-Updates
  TradeHistory.razor             – Trade-Historie mit Filtern
  Backtest.razor                – Backtesting-Konfiguration und Ergebnisse
  SettingsPage.razor            – Alle Einstellungen
```

## Sicherheitshinweis

- Immer zuerst mit Paper-Trading testen!
- Dieser Bot ist ein Entwicklungsprojekt – kein fertiges Handelssystem.
- Automatisiertes Trading birgt erhebliche finanzielle Risiken.
- API-Keys niemals committen – Secrets gehoeren in Umgebungsvariablen.
