# Verbesserungsplan

Priorisierte Uebersicht der Verbesserungen fuer Produktionsreife und Codequalitaet.

## P0 – Produktionsreife (Blocker)

| # | Verbesserung | Aufwand | Status |
|---|---|---|---|
| 1 | **Authentifizierung** – Cookie-basierte Auth, Benutzerverwaltung, Admin-Seed, erzwungene Passwortaenderung | 2-3 Tage | Erledigt |
| 2 | **Tests** – 84 Tests: PipCalculator, CorrelationMatrix, AuthService, RiskManager, GridTrading. 2 Bugs in PipCalculator aufgedeckt (USDCHF/XAUUSD Pattern-Matching) | 5-7 Tage | Erledigt |

## P1 – Strukturelle Qualitaet

| # | Verbesserung | Aufwand | Status |
|---|---|---|---|
| 3 | **Fehlende UI-Seiten** – Grid Dashboard, Pending Orders, Portfolio-Allokation, News & Kalender hinzugefuegt | 5-8 Tage | Erledigt |
| 4 | **Models.cs aufteilen** – 949 Zeilen in 7 fokussierte Dateien: Enums, Entities, ConfigSettings, Dtos, TradingCalculations, TradeLockerModels, ViewModels | 1-2 Tage | Erledigt |
| 5 | **TradeLockerService.cs aufteilen** – 1739 Zeilen in 4 partial classes: Core (488), MarketData (583), Account (328), Orders (363) | 2-3 Tage | Erledigt |
| 6 | **Silent Error Swallowing** – Leere catch-Bloecke in RiskManager, ReportService mit Debug-Logging versehen. Static-Methoden-Catches mit erklaerenden Kommentaren | 0.5 Tage | Erledigt |

## P2 – Operationale Exzellenz

| # | Verbesserung | Aufwand | Status |
|---|---|---|---|
| 7 | **Thread Safety** – `_accounts` mit Lock-Pattern, `_accountCts` als ConcurrentDictionary, `_partialClosedPositions` als ConcurrentDictionary | 1 Tag | Erledigt |
| 8 | **DB-Optimierung** – 4 Composite-Indexes (Trade, TradingLog), Daten-Retention (Logs > 90 Tage bei Startup loeschen) | 1-2 Tage | Erledigt |
| 9 | **Metriken** – Prometheus /metrics Endpunkt: Trade-Counter, LLM-Latenz-Histogram, Broker-Latenz, Portfolio-Gauge, Kill-Switch-Gauge, Rejection-Counter | 2 Tage | Erledigt |
| 10 | **Circuit Breaker** – Polly fuer externe APIs (TradeLocker, LLM, Finnhub), um Kaskadenfehler zu verhindern | 1-2 Tage | Offen |
| 11 | **Rate Limiting** auf API-Endpunkten (`/api/pnl-history`, `/api/trades/export`) | 0.5 Tage | Offen |

## P3 – Polish

| # | Verbesserung | Aufwand | Status |
|---|---|---|---|
| 12 | **Dead Code entfernen** – `SimulatedBrokerService` (Stock-Simulator, nie genutzt), `IBSettings` (Legacy) | 0.5 Tage | Offen |
| 13 | **UX** – Bestaetigungsdialoge fuer destruktive Aktionen, Pagination bei Trade-History | 1-2 Tage | Offen |

## Empfehlung

Reihenfolge: Authentifizierung (erledigt) → Tests → fehlende UI-Seiten → Rest nach Bedarf.
