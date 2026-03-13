# Claude Trading Bot - Feature Roadmap

Stand: 13.03.2026

---

## Phase 1: Gewinnschutz & Risk (Prioritaet: HOCH)

Ziel: Bestehende Gewinne schuetzen und Verluste praeziser begrenzen.

### 1.1 Trailing Stop-Loss

**Problem:** Aktuell wird ein fester SL gesetzt. Laeuft der Trade 100 Pips im Plus, bleibt der SL am Ursprung – bei Umkehr wird der gesamte Gewinn abgegeben.

**Loesung:**
- Neues Feld `TrailingStopDistance` in `RiskSettings` (z.B. 30 Pips)
- In `RiskManager.CheckStopLossesAsync()`: Wenn unrealisierter Gewinn > TrailingStopDistance, SL nachziehen
- TradeLocker API: `PATCH /trade/positions/{id}` um SL auf der Broker-Seite zu aktualisieren
- Neues Feld `CurrentStopLoss` in Position-Model zur Nachverfolgung

**Dateien:**
- `Models/Models.cs` – Position: +CurrentStopLoss, +TrailingStopDistance
- `Services/RiskManager.cs` – CheckStopLossesAsync erweitern
- `Services/TradeLockerService.cs` – Neue Methode: UpdatePositionSLAsync()
- `appsettings.json` – TrailingStopPips Konfiguration

**Aufwand:** ~2h

### 1.2 Breakeven-Stop

**Problem:** Sobald ein Trade z.B. 20 Pips im Plus ist, sollte der SL mindestens auf den Einstiegspreis (Breakeven) verschoben werden – so kann der Trade nicht mehr verlieren.

**Loesung:**
- Neues Setting `BreakevenTriggerPips` (z.B. 20)
- In CheckStopLossesAsync: Wenn Gewinn >= BreakevenTriggerPips und SL < Einstiegspreis → SL auf Einstiegspreis + 1 Pip setzen
- Wird VOR dem Trailing Stop geprueft (Trailing uebernimmt dann ab hoeheren Gewinnen)

**Dateien:**
- `Services/RiskManager.cs` – Breakeven-Logik in CheckStopLossesAsync
- `appsettings.json` – BreakevenTriggerPips

**Aufwand:** ~1h

### 1.3 Risiko-basierte Position Sizing

**Problem:** Aktuell wird die Positionsgroesse als % vom Portfolio berechnet. Besser: Risiko pro Trade = fester %-Satz, Positionsgroesse ergibt sich aus der SL-Distanz.

**Formel:**
```
Lots = (Portfolio * RisikoProTrade%) / (SL-Distanz in Pips * Pip-Wert pro Lot)
```
Beispiel: $25.000 * 1% = $250 Risiko. SL 30 Pips bei EURUSD (Pip-Wert $10/Lot) = 0.83 Lots → abgerundet 0.08 Lots.

**Loesung:**
- Neues Setting `RiskPerTradePercent` (z.B. 1.0)
- In TradingEngine: Wenn LLM einen SL liefert, Position Sizing per Risiko statt per Notional-Wert
- Fallback auf aktuelle Methode wenn kein SL gesetzt

**Dateien:**
- `Models/Models.cs` – RiskSettings: +RiskPerTradePercent, +PipValues Dictionary
- `Services/TradingEngine.cs` – CalculatePositionSize() Methode
- `Services/RiskManager.cs` – ValidateTradeAsync um SL-basierte Validierung erweitern

**Aufwand:** ~2h

---

## Phase 2: Bessere Analyse-Inputs (Prioritaet: HOCH)

Ziel: Dem LLM bessere Daten liefern fuer praezisere Empfehlungen.

### 2.1 Technische Indikatoren

**Problem:** Das LLM bekommt nur Rohpreise (Candles, Close-Werte). Technische Indikatoren wie RSI, EMA, MACD sind kompakter und informativer.

**Loesung:**
- Neuer Service `TechnicalAnalysisService` mit Berechnungen:
  - **RSI(14)** – Ueberkauft/Ueberverkauft
  - **EMA(20, 50, 200)** – Trendrichtung und Kreuzungen
  - **MACD(12, 26, 9)** – Momentum
  - **ATR(14)** – Volatilitaet (auch nützlich fuer SL-Berechnung)
  - **Bollinger Bands(20, 2)** – Volatilitaets-Baender
- Indikatoren als zusaetzlicher Abschnitt im LLM-Prompt
- Erweiterung von `ClaudeAnalysisRequest` um Indikator-Felder

**Dateien:**
- `Services/TechnicalAnalysisService.cs` – NEU: Indikator-Berechnungen
- `Models/Models.cs` – ClaudeAnalysisRequest: +Indicators
- `Services/ClaudePromptBuilder.cs` – Indikatoren-Abschnitt im Prompt
- `Services/TradingEngine.cs` – Indikatoren berechnen und uebergeben

**Aufwand:** ~4h

### 2.2 Session-Filter

**Problem:** Forex hat unterschiedliche Liquiditaet je nach Tageszeit. In der Asia-Session (nachts in Europa) sind die Spreads hoeher und Bewegungen unvorhersehbar.

**Loesung:**
- Konfigurierbare Trading-Sessions in appsettings.json:
  - London: 08:00-17:00 UTC
  - New York: 13:00-22:00 UTC
  - Overlap (beste Zeit): 13:00-17:00 UTC
- TradingEngine prüft vor jedem Zyklus ob aktuelle Zeit in erlaubter Session liegt
- Symbol-spezifisch: JPY-Pairs auch in Tokyo-Session (00:00-09:00 UTC)

**Dateien:**
- `Models/Models.cs` – TradingSessionSettings
- `Services/TradingEngine.cs` – Session-Check vor RunTradingCycleAsync
- `appsettings.json` – Session-Konfiguration

**Aufwand:** ~1.5h

### 2.3 Markt-Oeffnungszeiten-Erkennung

**Problem:** Forex-Maerkte sind Sonntag 22:00 UTC bis Freitag 22:00 UTC geoeffnet. Am Wochenende liefert TradeLocker keine Quotes, Spreads sind extrem und Orders koennen fehlschlagen. Aktuell laeuft der Bot blind weiter, verschwendet LLM-Aufrufe und produziert fehlerhafte Trades mit Preis 0.

**Loesung:**
- Neuer Service `MarketHoursService`:
  - `IsMarketOpen()` – Prüft ob Forex-Markt geoeffnet ist (So 22:00 – Fr 22:00 UTC)
  - `IsMarketOpen(string symbol)` – Symbol-spezifisch:
    - Forex (EURUSD, GBPUSD, etc.): So 22:00 – Fr 22:00 UTC
    - Indizes (US100, DE40): nur waehrend Boersen-Oeffnungszeiten
    - Gold/Oel: fast 24h aber mit kurzen Pausen
    - Crypto (falls spaeter): 24/7
  - `GetNextOpen()` – Wann oeffnet der Markt wieder (fuer Log/Dashboard)
  - `GetTimeUntilClose()` – Warnung wenn Markt bald schliesst (keine neuen Positionen in letzten 30 Min vor Schluss)
- Feiertage: Statische Liste fuer Weihnachten, Neujahr (Forex geschlossen)
- TradingEngine:
  - Vor `RunTradingCycleAsync()`: `if (!marketHours.IsMarketOpen()) skip`
  - Pro Symbol: `if (!marketHours.IsMarketOpen(symbol)) skip`
  - Log: "Markt geschlossen, naechste Oeffnung: Sonntag 22:00 UTC"
- PositionSyncService: Sync-Frequenz reduzieren wenn Markt geschlossen (alle 5 Min statt 30s)
- Dashboard: Badge "Markt geschlossen" / "Markt offen" + Countdown bis Oeffnung/Schliessung
- Preis-Validierung: Wenn GetCurrentPriceAsync 0 liefert, Symbol ueberspringen (Fallback fuer unbekannte Oeffnungszeiten)

**Dateien:**
- `Services/MarketHoursService.cs` – NEU: Markt-Oeffnungszeiten-Logik
- `Services/TradingEngine.cs` – Market-Hours-Check vor Analyse
- `Services/PositionSyncService.cs` – Reduzierte Sync-Frequenz bei geschlossenem Markt
- `Pages/Index.cshtml` – Markt-Status-Badge
- `appsettings.json` – Optionale Feiertage-Liste, Custom-Oeffnungszeiten

**Aufwand:** ~2.5h

### 2.4 News/Kalender-Filter

**Problem:** High-Impact-Events (NFP, FOMC, EZB-Zinsentscheid) verursachen extreme Volatilitaet. Trading davor/danach ist hochriskant.

**Loesung:**
- Kostenlose API: ForexFactory-Kalender oder FCS API
- Neuer Service `EconomicCalendarService`:
  - Taeglich Kalender abrufen und cachen
  - Methode `IsHighImpactEventNear(string currency, int minutesBefore = 30, int minutesAfter = 15)`
- TradingEngine: Wenn Event nahe, Symbol ueberspringen
- Dashboard: Naechste Events anzeigen

**Dateien:**
- `Services/EconomicCalendarService.cs` – NEU
- `Services/TradingEngine.cs` – Event-Check pro Symbol
- `Pages/Index.cshtml` – Naechste Events Widget
- `appsettings.json` – Calendar API URL, Buffer-Minuten

**Aufwand:** ~3h

---

## Phase 3: Dashboard & Monitoring (Prioritaet: MITTEL)

Ziel: Ueberblick ob die Strategie funktioniert, ohne staendig das Dashboard zu checken.

### 3.1 Equity-Kurve (Chart)

**Problem:** Kein visueller Ueberblick ueber die Portfolio-Entwicklung.

**Loesung:**
- Lightweight JavaScript Chart-Library: Chart.js (kein npm noetig, CDN)
- DailyPnL-Daten als Linien-Chart auf dem Dashboard
- X-Achse: Datum, Y-Achse: Portfolio-Wert
- Optional: Zweite Linie fuer Drawdown

**Dateien:**
- `Pages/Shared/_Layout.cshtml` – Chart.js CDN einbinden
- `Pages/Index.cshtml` – Chart-Container und JS-Code
- `Program.cs` – API-Endpunkt `/api/pnl-history` (JSON)

**Aufwand:** ~2h

### 3.2 Trade-Statistiken

**Problem:** Keine Uebersicht ueber Performance-Kennzahlen.

**Kennzahlen:**
- **Win-Rate**: % der profitablen Trades
- **Avg Win / Avg Loss**: Durchschnittlicher Gewinn/Verlust
- **Profit Factor**: Summe Gewinne / Summe Verluste (>1.5 = gut)
- **Max Drawdown**: Groesster Rueckgang vom Peak
- **Sharpe Ratio**: Risiko-adjustierte Rendite
- **Trades pro Tag/Woche**

**Loesung:**
- Neues ViewModel `TradingStatsViewModel` mit allen Kennzahlen
- Berechnung in `IndexModel.OnGetAsync()` aus Trade-Tabelle
- Anzeige als Stat-Cards auf dem Dashboard

**Dateien:**
- `Models/Models.cs` – TradingStatsViewModel
- `Pages/Index.cshtml.cs` – Statistik-Berechnung
- `Pages/Index.cshtml` – Stats-Sektion

**Aufwand:** ~2h

### 3.3 Notifications (Telegram)

**Problem:** Man muss das Dashboard manuell oeffnen um Trade-Aktivitaet zu sehen.

**Loesung:**
- Telegram Bot API (kostenlos, einfach)
- Neuer Service `NotificationService`:
  - `SendTradeNotificationAsync(Trade trade)`
  - `SendAlertAsync(string message)` (Kill Switch, grosse Verluste)
- Aufrufe in TradingEngine nach Trade-Ausfuehrung und RiskManager bei Kill Switch
- Konfigurierbar: welche Events notifiziert werden

**Dateien:**
- `Services/NotificationService.cs` – NEU
- `Services/TradingEngine.cs` – Notification nach Trade
- `Services/RiskManager.cs` – Notification bei Kill Switch
- `appsettings.json` – Telegram BotToken, ChatId
- `Pages/Settings.cshtml` – Telegram-Konfiguration

**Aufwand:** ~2h

---

## Phase 4: Robustheit (Prioritaet: MITTEL)

### 4.1 Drawdown-Tracking & Limits

**Problem:** Tagesverlust-Limit existiert, aber kein Tracking des maximalen Drawdowns vom Equity-Peak.

**Loesung:**
- In DailyPnL: `PeakEquity` Feld mitspeichern
- Neues Setting `MaxDrawdownPercent` (z.B. 10%)
- Kill Switch aktivieren wenn Drawdown vom Peak > MaxDrawdownPercent

**Dateien:**
- `Models/Models.cs` – DailyPnL: +PeakEquity
- `Services/RiskManager.cs` – Drawdown-Check in RecordDailyPnLAsync

**Aufwand:** ~1h

### 4.2 Korrelationscheck

**Problem:** EURUSD und GBPUSD sind stark korreliert. 5 Sell-Positionen in korrelierten Pairs = effektiv eine riesige Position.

**Loesung:**
- Statische Korrelationsmatrix fuer die Watchlist-Pairs:
  - EURUSD/GBPUSD: 0.85 (hoch korreliert)
  - EURUSD/USDJPY: -0.50 (invers)
  - XAUUSD/EURUSD: 0.40 (moderat)
- In RiskManager.ValidateTradeAsync: Summe der korrelierten Exposure berechnen
- Ablehnen wenn korrelierte Gesamtposition > MaxCorrelatedExposurePercent

**Dateien:**
- `Services/RiskManager.cs` – Korrelationscheck
- `Models/Models.cs` – Korrelationsmatrix-Konfiguration

**Aufwand:** ~2h

### 4.3 Weekly/Monthly Loss Limits

**Problem:** Nur taesgliche Verlustgrenze. Eine Woche mit 5x -3% = -15% ohne Kill Switch.

**Loesung:**
- Neue Settings: `MaxWeeklyLossPercent`, `MaxMonthlyLossPercent`
- In RiskManager: Letzten 7/30 Tage DailyPnL summieren

**Dateien:**
- `Services/RiskManager.cs` – Erweiterte Verlust-Checks
- `appsettings.json` – Wochen-/Monatslimits

**Aufwand:** ~1h

---

## Phase 5: Fortgeschritten (Prioritaet: NIEDRIG)

### 5.1 Backtesting

**Problem:** Keine Moeglichkeit die Strategie auf historischen Daten zu testen.

**Loesung:**
- Neuer Service `BacktestEngine`:
  - Historische Candles von TradeLocker laden (laengerer Zeitraum)
  - TradingEngine-Logik simulieren mit gespeicherten Daten
  - Ergebnis: Equity-Kurve, Win-Rate, Drawdown
- Neue Seite `/Backtest` mit Konfiguration und Ergebnis-Anzeige

**Aufwand:** ~8h

### 5.2 Paper-Trading-Modus

**Problem:** Kein risikofreier Testmodus mit echten Marktdaten.

**Loesung:**
- Toggle in Settings: "Paper Trading"
- Wenn aktiv: PlaceOrderAsync und ClosePositionAsync nur lokal simulieren
- Echte Marktdaten von TradeLocker, aber keine echten Orders

**Aufwand:** ~3h

### 5.3 Multi-Timeframe-Bestaetigung

**Problem:** LLM koennte auf 1H-Chart einen Buy empfehlen, waehrend der 4H/1D-Trend klar abwaerts zeigt.

**Loesung:**
- Separate LLM-Aufrufe fuer verschiedene Timeframes
- Nur traden wenn mindestens 2 von 3 Timeframes uebereinstimmen
- Alternativ: Trend-Indikator (EMA200) auf hoeherem Timeframe als Filter

**Aufwand:** ~3h

### 5.4 Auto-Reconnect

**Problem:** Wenn TradeLocker-Verbindung abbricht (Token abgelaufen, Netzwerk), bleibt der Bot stehen.

**Loesung:**
- In TradingEngine: Wenn ConnectAsync fehlschlaegt, exponentielles Backoff (5s, 10s, 30s, 60s)
- Token-Refresh ist schon implementiert, aber kein Reconnect bei komplettem Verbindungsverlust

**Aufwand:** ~1h

---

## Zusammenfassung

| Phase | Feature | Aufwand | Prioritaet |
|-------|---------|---------|------------|
| 1.1 | Trailing Stop-Loss | ~2h | HOCH |
| 1.2 | Breakeven-Stop | ~1h | HOCH |
| 1.3 | Risiko-basierte Position Sizing | ~2h | HOCH |
| 2.1 | Technische Indikatoren | ~4h | HOCH |
| 2.2 | Session-Filter | ~1.5h | HOCH |
| 2.3 | Markt-Oeffnungszeiten-Erkennung | ~2.5h | HOCH |
| 2.4 | News/Kalender-Filter | ~3h | HOCH |
| 3.1 | Equity-Kurve (Chart) | ~2h | MITTEL |
| 3.2 | Trade-Statistiken | ~2h | MITTEL |
| 3.3 | Notifications (Telegram) | ~2h | MITTEL |
| 4.1 | Drawdown-Tracking | ~1h | MITTEL |
| 4.2 | Korrelationscheck | ~2h | MITTEL |
| 4.3 | Weekly/Monthly Loss Limits | ~1h | MITTEL |
| 5.1 | Backtesting | ~8h | NIEDRIG |
| 5.2 | Paper-Trading-Modus | ~3h | NIEDRIG |
| 5.3 | Multi-Timeframe-Bestaetigung | ~3h | NIEDRIG |
| 5.4 | Auto-Reconnect | ~1h | NIEDRIG |
| | **Gesamt** | **~41h** | |

Empfohlene Reihenfolge: Phase 1 → 2.3 → 2.1 → 3.3 → 3.1+3.2 → Rest nach Bedarf.
