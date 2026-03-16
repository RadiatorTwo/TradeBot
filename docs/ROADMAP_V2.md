# Claude Trading Bot - Roadmap V2

Stand: 14.03.2026 | Aufbauend auf der abgeschlossenen V1-Roadmap (17/17 Features)

---

## Phase 6: Intelligentere Strategie (Prioritaet: HOCH)

Ziel: Das LLM besser nutzen und die Entscheidungsqualitaet messbar verbessern.

### 6.1 LLM-Prompt-Optimierung mit Feedback-Loop

**Problem:** Das LLM bekommt keine Rueckmeldung ueber die Qualitaet seiner frueheren Empfehlungen. Es lernt nicht aus Fehlern.

**Loesung:**
- Letzte 10 geschlossene Trades mit PnL-Ergebnis im Prompt mitgeben
- "Deine letzten 3 Trades fuer EURUSD waren: +15 Pips, -8 Pips, +22 Pips. Win-Rate: 67%"
- LLM kann eigene Muster erkennen und Strategie anpassen
- Neuer Abschnitt `BuildTradeHistoryContext()` in ClaudePromptBuilder

**Dateien:**
- `Services/ClaudePromptBuilder.cs` – Neuer Abschnitt mit Trade-Historie
- `Services/TradingEngine.cs` – Letzte Trades laden und uebergeben
- `Models/Models.cs` – ClaudeAnalysisRequest: +RecentTradeResults

**Aufwand:** ~2h

### 6.2 Sentiment-Analyse (News-Headlines)

**Problem:** Das LLM hat keinen Zugang zu aktuellen Nachrichten die den Markt bewegen koennten.

**Loesung:**
- Kostenlose News-APIs: NewsAPI.org, Finnhub News, oder RSS-Feeds (Reuters, Bloomberg)
- Neuer Service `NewsSentimentService`:
  - Taeglich Top-Headlines pro Waehrung abrufen
  - Headlines als zusaetzlichen Kontext im LLM-Prompt
  - LLM bewertet Sentiment selbst (kein separates ML-Modell noetig)
- Konfigurierbar: welche Quellen, wie viele Headlines

**Dateien:**
- `Services/NewsSentimentService.cs` – NEU
- `Services/ClaudePromptBuilder.cs` – News-Abschnitt im Prompt
- `appsettings.json` – NewsAPI Key, Quellen-Konfiguration

**Aufwand:** ~3h

### 6.3 Dynamischer Confidence-Threshold

**Problem:** MinConfidence ist statisch (z.B. 0.65). In volatilen Maerkten sollte der Schwellenwert hoeher sein, in ruhigen Maerkten koennte er niedriger sein.

**Loesung:**
- ATR-basierte Anpassung: Wenn ATR > Durchschnitt → Confidence-Schwelle erhoehen
- Drawdown-basierte Anpassung: Im Drawdown konservativer traden
- Win-Rate-basierte Anpassung: Bei sinkender Win-Rate Schwelle erhoehen
- Formel: `effectiveMinConfidence = base + atrFactor + drawdownFactor + streakFactor`

**Dateien:**
- `Services/RiskManager.cs` – GetDynamicMinConfidence()
- `Services/TradingEngine.cs` – Dynamische Confidence nutzen
- `appsettings.json` – Basis-Confidence und Faktoren

**Aufwand:** ~2h

### 6.4 Partial Close / Pyramiding

**Problem:** Aktuell wird eine Position komplett geoeffnet oder geschlossen. Kein Teilgewinn-Mitnehmen, kein Nachkaufen bei Bestaetigung.

**Loesung:**
- **Partial Close:** Bei Erreichen von TP1 (z.B. 50% der Position schliessen), Rest mit Trailing SL laufen lassen
- **Pyramiding:** Bei Trend-Bestaetigung (z.B. EMA-Kreuzung nach Entry) Position vergroessern
- Neues Feld `PartialClosePercent` und `MaxPyramidLevels` in RiskSettings
- LLM kann "partial_close" als Action empfehlen

**Dateien:**
- `Models/Models.cs` – RiskSettings: +PartialClosePercent, +MaxPyramidLevels
- `Services/TradingEngine.cs` – Partial Close und Pyramid-Logik
- `Services/RiskManager.cs` – Pyramid-Validierung
- `Services/ClaudePromptBuilder.cs` – LLM ueber Partial Close informieren

**Aufwand:** ~4h

---

## Phase 7: Multi-Account & Skalierung (Prioritaet: HOCH)

Ziel: Mehrere Konten und Strategien gleichzeitig verwalten.

### 7.1 Multi-Account-Support

**Problem:** Aktuell nur ein TradeLocker-Konto. Professionelle Trader haben Demo + Live + ggf. mehrere Strategien.

**Loesung:**
- Konfiguration: Array von TradeLocker-Accounts in appsettings.json
- Pro Account: eigene TradingEngine-Instanz, eigene RiskSettings
- Dashboard: Account-Switcher / Gesamtuebersicht
- DB: AccountId-Feld in Trade, Position, DailyPnL

**Dateien:**
- `Models/Models.cs` – Trade/Position/DailyPnL: +AccountId
- `Services/TradingEngineFactory.cs` – NEU: Erstellt TradingEngine pro Account
- `Program.cs` – Multi-Account-Registration
- `Components/Pages/Dashboard.razor` – Account-Switcher

**Aufwand:** ~6h

### ~~7.2 REST-API fuer externe Integration~~ (ENTFERNT – kein externer Zugriff, alles ueber WebInterface)

### 7.3 Strategie-Rotation / A-B-Testing

**Problem:** Keine Moeglichkeit verschiedene Prompt-Varianten oder Parameter-Sets gegeneinander zu testen.

**Loesung:**
- Mehrere "Strategie-Profile" definierbar (z.B. konservativ vs. aggressiv)
- Jedes Profil hat eigene RiskSettings, eigenen Prompt-Template, eigene Watchlist
- Paper-Trading-Modus fuer Strategie B waehrend Strategie A live laeuft
- Vergleichs-Dashboard: Strategie A vs. B Performance

**Dateien:**
- `Models/Models.cs` – StrategyProfile
- `Services/StrategyManager.cs` – NEU: Profil-Verwaltung
- `Components/Pages/StrategyComparison.razor` – NEU
- `appsettings.json` – Strategien-Array

**Aufwand:** ~5h

---

## Phase 8: Datenqualitaet & Analyse (Prioritaet: MITTEL)

Ziel: Bessere Daten fuehren zu besseren Entscheidungen.

### 8.1 Spread-Tracking & Spread-Filter

**Problem:** Der Bot beruecksichtigt den Spread nicht. Ein Trade mit 2 Pips Ziel aber 3 Pips Spread ist garantiert ein Verlust.

**Loesung:**
- Spread = Ask - Bid (wird schon abgerufen, aber nicht genutzt)
- Neues Setting `MaxSpreadPips` – Trade ablehnen wenn Spread zu hoch
- Spread im LLM-Prompt erwaehnen: "Aktueller Spread: 1.8 Pips"
- Spread-Historie loggen fuer Analyse

**Dateien:**
- `Services/RiskManager.cs` – Spread-Check in ValidateTradeAsync
- `Services/ClaudePromptBuilder.cs` – Spread im Prompt
- `Models/Models.cs` – RiskSettings: +MaxSpreadPips
- `Models/Models.cs` – Trade: +SpreadAtEntry

**Aufwand:** ~1.5h

### 8.2 Trade-Journal mit Tags & Notizen

**Problem:** Keine Moeglichkeit Trades zu annotieren, kategorisieren oder Muster zu erkennen.

**Loesung:**
- Neue Felder in Trade: Tags (Array), Notes (Freitext), SetupType (z.B. "EMA-Cross", "Breakout")
- LLM liefert SetupType automatisch in der Empfehlung
- Dashboard: Filter nach Tags/SetupType, Performance pro Setup-Typ
- Manuelles Hinzufuegen von Notizen ueber Trade-Detail-Seite

**Dateien:**
- `Models/Models.cs` – Trade: +Tags, +Notes, +SetupType
- `Components/Pages/TradeJournal.razor` – NEU: Journal-Ansicht
- `Services/ClaudePromptBuilder.cs` – SetupType im Response-Schema
- Migration fuer neue DB-Felder

**Aufwand:** ~3h

### 8.3 Performance-Report (PDF/Email)

**Problem:** Keine Moeglichkeit einen taeglichen/woechentlichen Performance-Bericht zu erhalten.

**Loesung:**
- Taeglicher Report per Telegram (oder Email):
  - Heutige Trades, PnL, Win-Rate
  - Aktuelle Positionen und unrealisierter PnL
  - Drawdown-Status
  - Top/Flop Symbole
- Woechentlicher Report mit Vergleich zur Vorwoche
- Optional: PDF-Export fuer Steuer/Dokumentation

**Dateien:**
- `Services/ReportService.cs` – NEU: Report-Generierung
- `Services/NotificationService.cs` – Report-Versand erweitern
- `appsettings.json` – Report-Zeitplan (z.B. taeglich 22:00 UTC)

**Aufwand:** ~3h

### 8.4 Dynamische Korrelationsmatrix

**Problem:** Korrelationen sind statisch hinterlegt. Reale Korrelationen aendern sich ueber Zeit.

**Loesung:**
- Historische Preise der Watchlist-Pairs laden (letzte 30 Tage)
- Pearson-Korrelation dynamisch berechnen
- Korrelationsmatrix taeglich aktualisieren
- Dashboard: Korrelations-Heatmap

**Dateien:**
- `Services/CorrelationService.cs` – NEU: Dynamische Berechnung
- `Services/RiskManager.cs` – Dynamische statt statische Matrix nutzen
- `Components/Shared/CorrelationHeatmap.razor` – NEU

**Aufwand:** ~3h

---

## Phase 9: Operational Excellence (Prioritaet: MITTEL)

Ziel: Zuverlaessiger Betrieb und einfache Wartung.

### 9.1 Health-Checks & Monitoring

**Problem:** Kein automatischer Check ob alle Services laufen. Keine Alertierung wenn etwas ausfaellt.

**Loesung:**
- ASP.NET Health Checks:
  - TradeLocker-Verbindung
  - LLM-Provider erreichbar
  - Datenbank-Verbindung
  - Letzte Trade-Aktivitaet (Warnung wenn > 2h kein Trade)
- `/health` Endpunkt fuer externe Monitoring-Tools (UptimeRobot, etc.)
- Telegram-Alert bei Health-Check-Failure

**Dateien:**
- `HealthChecks/TradeLockerHealthCheck.cs` – NEU
- `HealthChecks/LlmHealthCheck.cs` – NEU
- `Program.cs` – Health Check Registration
- `Services/NotificationService.cs` – Health-Alert

**Aufwand:** ~2h

### 9.2 Structured Logging & Log-Rotation

**Problem:** Logs nur in DB und Konsole. Keine strukturierte Suche, kein Log-Rotation.

**Loesung:**
- Serilog mit JSON-Structured-Logging
- File Sink mit taeglicher Rotation (max 7 Tage)
- Optional: Seq oder Loki fuer Log-Aggregation
- Correlation-ID pro Trading-Zyklus fuer Trace-Ability

**Dateien:**
- `Program.cs` – Serilog-Konfiguration
- `ClaudeTradingBot.csproj` – Serilog NuGet Pakete
- `appsettings.json` – Serilog-Konfiguration

**Aufwand:** ~1.5h

### 9.3 Graceful Shutdown & State Recovery

**Problem:** Bei Neustart gehen laufende Trades nicht verloren, aber der Bot weiss nicht welche SL/TP-Levels er gesetzt hat.

**Loesung:**
- Vor Shutdown: Alle laufenden State-Infos in DB persistieren
- Nach Start: Offene Positionen vom Broker laden und mit DB abgleichen
- Recovery-Log: "3 Positionen wiederhergestellt nach Neustart"
- Optional: Pending Orders pruefen und ggf. stornieren

**Dateien:**
- `Services/TradingEngine.cs` – OnStoppingAsync, StateRecovery
- `Services/PositionSyncService.cs` – Startup-Sync erweitern

**Aufwand:** ~2h

---

## Phase 10: Erweiterte Strategien (Prioritaet: NIEDRIG)

Ziel: Ueber einfache LLM-Empfehlungen hinausgehen.

### 10.1 Grid-Trading-Modus

**Problem:** LLM-basiertes Trading funktioniert gut bei Trends, aber nicht in Seitwaertsmaerkten.

**Loesung:**
- Grid-Strategie: Kauf/Verkauf-Orders in regelmaessigen Abstaenden platzieren
- Konfiguration: Grid-Abstand (Pips), Grid-Levels, Lot-Groesse pro Level
- LLM bestimmt ob Grid-Trading oder Trend-Trading besser passt
- Automatische Grid-Verwaltung: neue Orders nachkaufen wenn Levels ausgeloest

**Aufwand:** ~6h

### 10.2 Order-Typen erweitern (Limit/Stop Orders)

**Problem:** Aktuell nur Market-Orders. Kein Einstieg zu bestimmten Preisen moeglich.

**Loesung:**
- LLM kann Limit-Order empfehlen: "Buy EURUSD at 1.0800 (aktuell 1.0850)"
- Neuer Action-Typ: "buy_limit", "sell_limit", "buy_stop", "sell_stop"
- Order-Management: Pending Orders tracken, stornieren wenn veraltet
- TradeLocker API unterstuetzt bereits Limit/Stop-Orders

**Aufwand:** ~4h

### 10.3 Portfolio-Rebalancing

**Problem:** Keine automatische Anpassung der Gesamt-Allokation ueber alle Positionen.

**Loesung:**
- Ziel-Allokation pro Symbol definierbar (z.B. max 30% in Gold, max 20% pro Forex-Pair)
- Automatische Reduktion uebergewichteter Positionen
- LLM bekommt aktuelle Allokation im Prompt

**Aufwand:** ~3h

### 10.4 Backtesting mit LLM

**Problem:** Aktuelles Backtesting nutzt nur EMA-Cross/RSI-Reversal, nicht das LLM selbst.

**Loesung:**
- Historische Candles durchlaufen und pro Zeitschritt LLM-Analyse anfragen
- Langsam (LLM-Kosten!) aber realistischster Test der tatsaechlichen Strategie
- Batch-Modus: mehrere Zeitschritte in einem LLM-Call zusammenfassen
- Rate-Limiting und Kosten-Tracking

**Aufwand:** ~6h

---

## Zusammenfassung

| Phase | Feature | Aufwand | Prioritaet |
|-------|---------|---------|------------|
| 6.1 | LLM-Feedback-Loop | ~2h | HOCH |
| 6.2 | Sentiment-Analyse | ~3h | HOCH |
| 6.3 | Dynamischer Confidence-Threshold | ~2h | HOCH |
| 6.4 | Partial Close / Pyramiding | ~4h | HOCH |
| 7.1 | Multi-Account-Support | ~6h | HOCH |
| ~~7.2~~ | ~~REST-API & Webhooks~~ | ~~~4h~~ | ENTFERNT |
| 7.3 | Strategie-Rotation / A-B-Testing | ~5h | HOCH |
| 8.1 | Spread-Tracking & Filter | ~1.5h | MITTEL |
| 8.2 | Trade-Journal | ~3h | MITTEL |
| 8.3 | Performance-Report | ~3h | MITTEL |
| 8.4 | Dynamische Korrelationsmatrix | ~3h | MITTEL |
| 9.1 | Health-Checks & Monitoring | ~2h | MITTEL |
| 9.2 | Structured Logging | ~1.5h | MITTEL |
| 9.3 | Graceful Shutdown & Recovery | ~2h | MITTEL |
| 10.1 | Grid-Trading | ~6h | NIEDRIG |
| 10.2 | Limit/Stop Orders | ~4h | NIEDRIG |
| 10.3 | Portfolio-Rebalancing | ~3h | NIEDRIG |
| 10.4 | Backtesting mit LLM | ~6h | NIEDRIG |
| | **Gesamt** | **~56h** | |

Empfohlene Reihenfolge: **6.1 → 8.1 → 6.3 → 6.2 → 6.4 → 7.1 → 7.3 → Rest nach Bedarf.**

Begruendung: Zuerst die Entscheidungsqualitaet verbessern (Feedback-Loop, Spread-Filter, dynamische Confidence), dann Skalierung (Multi-Account), dann erweiterte Features.
