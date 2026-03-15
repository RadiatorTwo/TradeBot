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
| 3 | **Fehlende UI-Seiten** – Grid Dashboard, Pending Orders, Portfolio-Allokation, News/Sentiment haben Backend aber keine Oberflaeche | 5-8 Tage | Offen |
| 4 | **Models.cs aufteilen** – 949 Zeilen mit 40+ Klassen (Entities, DTOs, Config, ViewModels, Utilities) in fokussierte Dateien splitten | 1-2 Tage | Offen |
| 5 | **TradeLockerService.cs aufteilen** – ~2000 Zeilen, mischt Auth, Marktdaten, Orders in Auth/MarketData/Order-Services splitten | 2-3 Tage | Offen |
| 6 | **Silent Error Swallowing** – 6 leere `catch {}`-Bloecke, u.a. in kritischen Pfaden (Dashboard, RiskManager, GridModels) | 0.5 Tage | Offen |

## P2 – Operationale Exzellenz

| # | Verbesserung | Aufwand | Status |
|---|---|---|---|
| 7 | **Thread Safety** – `_accounts` ist eine `List<>` die concurrent gelesen/geschrieben wird, `_partialClosedPositions` unsynchronisiert | 1 Tag | Offen |
| 8 | **DB-Optimierung** – Composite-Indexes, Daten-Retention (Logs aelter als 90 Tage loeschen), evtl. PostgreSQL | 1-2 Tage | Offen |
| 9 | **Metriken** – OpenTelemetry/Prometheus Counters fuer Trades, Rejections, LLM-Latenz, Broker-API-Latenz | 2 Tage | Offen |
| 10 | **Circuit Breaker** – Polly fuer externe APIs (TradeLocker, LLM, Finnhub), um Kaskadenfehler zu verhindern | 1-2 Tage | Offen |
| 11 | **Rate Limiting** auf API-Endpunkten (`/api/pnl-history`, `/api/trades/export`) | 0.5 Tage | Offen |

## P3 – Polish

| # | Verbesserung | Aufwand | Status |
|---|---|---|---|
| 12 | **Dead Code entfernen** – `SimulatedBrokerService` (Stock-Simulator, nie genutzt), `IBSettings` (Legacy) | 0.5 Tage | Offen |
| 13 | **UX** – Bestaetigungsdialoge fuer destruktive Aktionen, Pagination bei Trade-History | 1-2 Tage | Offen |

## Empfehlung

Reihenfolge: Authentifizierung (erledigt) → Tests → fehlende UI-Seiten → Rest nach Bedarf.
