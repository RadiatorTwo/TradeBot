# Claude Trading Bot – Projektplan für Cursor

## Projektübersicht

**Ziel:** Eine ASP.NET 8 Anwendung, die Claude als KI-Analyse-Engine nutzt und über die TradeLocker REST API Forex/CFD-Trades automatisch ausführt. Die App läuft als Daemon auf einem Linux-VPS und bietet ein Web-Dashboard zur Überwachung und Steuerung.

**Tech-Stack:**
- .NET 8 / C# / ASP.NET Razor Pages
- TradeLocker REST API (JWT-Auth, Demo + Live)
- Anthropic Claude API (claude-sonnet-4-20250514)
- SQLite + Entity Framework Core
- Serilog für Logging
- VPS-Deployment (Linux, z.B. Hetzner)

---

## Architektur

```
┌──────────────────────────────────────────────────────────────┐
│                   ASP.NET Web Dashboard                       │
│   (Portfolio, Positionen, Trades, Logs, Einstellungen)       │
│   Port 5000 – Razor Pages + Minimal API Endpoints            │
├──────────────────────────────────────────────────────────────┤
│                 TradingEngine (BackgroundService)             │
│                                                              │
│  ┌─────────────┐  ┌──────────────────┐  ┌────────────────┐  │
│  │ ClaudeService│  │ TradeLockerService│  │ RiskManager    │  │
│  │ (Analyse)    │◄─┤ (Broker API)      │◄─┤ (Stop-Loss,   │  │
│  │              │  │                   │  │  Kill Switch)  │  │
│  └─────────────┘  └──────────────────┘  └────────────────┘  │
├──────────────────────────────────────────────────────────────┤
│              SQLite (Trades, Positionen, PnL, Logs)          │
└──────────────────────────────────────────────────────────────┘
```

---

## TradeLocker API – Referenz

**Base URLs:**
- Demo: `https://demo.tradelocker.com/backend-api/`
- Live: `https://live.tradelocker.com/backend-api/`

**Authentifizierung:** JWT Token via `/auth/jwt/token`
- POST mit `email`, `password`, `server` → erhält `accessToken` + `refreshToken`
- Token als Header: `Authorization: Bearer {accessToken}`
- Jeder `/trade/*` Request braucht zusätzlich `accNum` im Header

**Wichtige Endpoints:**

| Endpoint | Methode | Beschreibung |
|----------|---------|--------------|
| `/auth/jwt/token` | POST | Login, JWT Token holen |
| `/auth/jwt/refresh` | POST | Token erneuern |
| `/auth/jwt/all-accounts` | GET | Alle Konten + accNum abrufen |
| `/trade/accounts/{id}/orders` | GET | Offene Orders abrufen |
| `/trade/accounts/{id}/orders` | POST | Neue Order erstellen |
| `/trade/accounts/{id}/positions` | GET | Offene Positionen abrufen |
| `/trade/positions/{positionId}` | DELETE | Position schließen (qty=0 für komplett) |
| `/trade/accounts/{id}/ordersHistory` | GET | Order-Historie |
| `/trade/history` | GET | Historische Kursdaten |
| `/trade/quotes` | GET | Aktuelle Kurse |
| `/trade/instruments` | GET | Verfügbare Instrumente |
| `/trade/accounts/{id}/details` | GET | Kontostand, Equity, Margin |
| `/trade/config` | GET | Rate Limits und Feld-Spezifikationen |

**Order erstellen (POST /trade/accounts/{id}/orders):**
```json
{
  "price": 0,
  "qty": 0.01,
  "routeId": "TRADE",
  "side": "buy",
  "stopLoss": 1.0800,
  "stopLossType": "absolute",
  "takeProfit": 1.1200,
  "takeProfitType": "absolute",
  "trStopOffset": 0,
  "tradableInstrumentId": 12345,
  "type": "market",
  "validity": "IOC"
}
```

**Position schließen (DELETE /trade/positions/{positionId}):**
- `qty=0` → Position komplett schließen
- `qty=N` → Teilweise schließen (N Lots)

**Wichtig:** Order → Position Mapping:
- Eine Order wird zu einer Position, sobald sie gefüllt wird
- orderId ≠ positionId – Mapping über `/ordersHistory`
- routeId: `TRADE` für Orders, `INFO` für Quotes/History

**Rate Limits:** Abrufbar über `/trade/config`, typisch 2 Requests/Sekunde pro Route.

**API-Dokumentation:** https://public-api.tradelocker.com/

---

## Projekt-Struktur (Solution)

```
ClaudeTradingBot/
├── ClaudeTradingBot.csproj
├── Program.cs                          # DI, Middleware, Minimal API
├── appsettings.json                    # Konfiguration
├── appsettings.Development.json
│
├── Models/
│   ├── Domain.cs                       # Trade, Position, DailyPnL, TradingLog
│   ├── Configuration.cs                # AnthropicSettings, TradeLockerSettings, RiskSettings
│   ├── Claude/
│   │   ├── ClaudeAnalysisRequest.cs    # Was an Claude geschickt wird
│   │   └── ClaudeTradeRecommendation.cs # Was Claude zurückgibt (JSON)
│   └── TradeLocker/
│       ├── AuthResponse.cs             # JWT Token Response
│       ├── AccountInfo.cs              # Kontodetails
│       ├── InstrumentInfo.cs           # Instrument-Daten
│       ├── OrderRequest.cs             # Order-Payload
│       ├── OrderResponse.cs            # Order-Ergebnis
│       └── PositionInfo.cs             # Positions-Daten
│
├── Data/
│   └── TradingDbContext.cs             # EF Core Context
│
├── Services/
│   ├── Interfaces/
│   │   ├── IBrokerService.cs           # Broker-Abstraktion
│   │   ├── IClaudeService.cs           # Claude-Analyse-Interface
│   │   └── IRiskManager.cs             # Risk-Management-Interface
│   ├── TradeLockerService.cs           # TradeLocker REST API Client
│   ├── ClaudeService.cs                # Claude API Integration
│   ├── RiskManager.cs                  # Stop-Loss, Kill Switch, Limits
│   └── TradingEngine.cs                # BackgroundService – Hauptloop
│
├── Pages/
│   ├── Index.cshtml / .cshtml.cs       # Dashboard
│   ├── Trades.cshtml / .cshtml.cs      # Trade-Historie
│   ├── Settings.cshtml / .cshtml.cs    # Einstellungen
│   └── Shared/
│       └── _Layout.cshtml              # Gemeinsames Layout
│
└── wwwroot/
    └── css/
        └── site.css                    # Dashboard-Styles
```

---

## Phasen-Plan

### Phase 1: Grundgerüst + TradeLocker Auth (Tag 1-2)

**Ziel:** Projekt aufsetzen, TradeLocker-Authentifizierung funktioniert.

**Aufgaben:**

1. **Projekt erstellen**
   - `dotnet new webapp -n ClaudeTradingBot`
   - NuGet-Pakete installieren:
     ```
     Microsoft.EntityFrameworkCore.Sqlite 8.0.*
     Microsoft.EntityFrameworkCore.Design 8.0.*
     Serilog.AspNetCore 8.0.*
     Serilog.Sinks.File 6.0.*
     Serilog.Sinks.Console 6.0.*
     ```

2. **Models anlegen**
   - Alle Domain-Modelle (Trade, Position, DailyPnL, TradingLog)
   - Konfigurationsklassen (TradeLockerSettings, AnthropicSettings, RiskSettings)
   - TradeLocker DTOs für Auth, Orders, Positions

3. **appsettings.json konfigurieren**
   ```json
   {
     "TradeLocker": {
       "BaseUrl": "https://demo.tradelocker.com/backend-api",
       "Email": "DEIN_EMAIL",
       "Password": "DEIN_PASSWORT",
       "Server": "DEIN_SERVER_NAME",
       "AccountId": "123456"
     },
     "Anthropic": {
       "ApiKey": "sk-ant-...",
       "Model": "claude-sonnet-4-20250514",
       "MaxTokens": 2048
     },
     "RiskManagement": {
       "MaxPositionSizePercent": 5.0,
       "MaxDailyLossPercent": 3.0,
       "StopLossPercent": 2.0,
       "MaxOpenPositions": 5,
       "TradingIntervalMinutes": 15,
       "KillSwitchEnabled": true,
       "MaxDailyLossAbsolute": 200.0,
       "MinConfidence": 0.65
     }
   }
   ```

4. **TradeLockerService – Auth implementieren**
   - `POST /auth/jwt/token` – Login mit Email/Password/Server
   - JWT Token speichern + automatisch refreshen
   - `GET /auth/jwt/all-accounts` – accNum ermitteln
   - HttpClient mit DelegatingHandler für automatisches Token-Management

**Akzeptanzkriterium:** `dotnet run` startet, Auth-Token wird geholt, accNum wird geloggt.

---

### Phase 2: TradeLocker Marktdaten + Konto (Tag 3-4)

**Ziel:** Kurse abrufen, Konto-Infos lesen, Instrumente laden.

**Aufgaben:**

1. **Instrumente laden**
   - `GET /trade/instruments` – alle handelbaren Instrumente cachen
   - Mapping: Symbolname (z.B. "EURUSD") → tradableInstrumentId
   - Beim Start einmal laden, dann im Speicher halten

2. **Kursdaten abrufen**
   - `GET /trade/quotes` – aktuelle Bid/Ask-Preise
   - `GET /trade/history` – historische Candles (1D, 4H, 1H Auflösungen)
   - Methode: `GetPriceHistoryAsync(instrumentId, resolution, lookback)`

3. **Konto-Infos**
   - `GET /trade/accounts/{id}/details` – Balance, Equity, Margin, freie Margin
   - `GET /trade/accounts/{id}/positions` – offene Positionen
   - `GET /trade/accounts/{id}/orders` – offene Orders

4. **EF Core Setup**
   - DbContext mit allen Entitäten
   - SQLite-Connection in appsettings
   - `Database.EnsureCreatedAsync()` beim Start

**Akzeptanzkriterium:** App loggt beim Start: Kontostand, Equity, Anzahl Instrumente, aktuelle EURUSD-Quote.

---

### Phase 3: Claude API Integration (Tag 5-6)

**Ziel:** Claude analysiert Marktdaten und gibt strukturierte Trading-Empfehlungen.

**Aufgaben:**

1. **ClaudeService implementieren**
   - HttpClient → `POST https://api.anthropic.com/v1/messages`
   - Header: `x-api-key`, `anthropic-version: 2023-06-01`
   - System-Prompt mit klarer JSON-Anweisung

2. **Analyse-Prompt bauen**
   - Input: Symbol, aktueller Kurs, Bid/Ask, historische Candles, Kontostand, offene Positionen
   - Claude soll antworten als:
     ```json
     {
       "symbol": "EURUSD",
       "action": "buy|sell|hold",
       "quantity": 0.01,
       "confidence": 0.82,
       "reasoning": "Technische Analyse zeigt...",
       "stopLoss": 1.0800,
       "takeProfit": 1.1200
     }
     ```

3. **JSON-Parsing mit Fehlerbehandlung**
   - ```json...``` Wrapper entfernen
   - Fallback bei ungültigem JSON
   - Logging der Empfehlung

4. **Prompt-Optimierung**
   - System-Prompt auf Deutsch oder Englisch (nach Präferenz)
   - Technische Indikatoren einbauen (SMA, RSI berechnen und mitschicken)
   - Portfolio-Kontext mitgeben

**Akzeptanzkriterium:** Claude bekommt EURUSD-Daten und gibt eine valide JSON-Empfehlung zurück.

---

### Phase 4: Risk Manager + Order-Execution (Tag 7-9)

**Ziel:** Trades werden validiert, ausgeführt und in der DB geloggt.

**Aufgaben:**

1. **RiskManager implementieren**
   - Validierung vor jedem Trade:
     - Kill Switch aktiv? → Ablehnen
     - Confidence < Schwelle? → Ablehnen
     - Positionsgröße > Max %? → Ablehnen
     - Max offene Positionen erreicht? → Ablehnen
     - Tagesverlust überschritten? → Kill Switch aktivieren
   - Stop-Loss Check: Alle offenen Positionen prüfen
   - Tages-PnL aufzeichnen

2. **Order-Execution im TradeLockerService**
   - `PlaceOrderAsync(symbol, side, quantity, stopLoss, takeProfit)`
     - Symbol → tradableInstrumentId auflösen
     - `POST /trade/accounts/{id}/orders` mit Market-Order
     - orderId aus Response speichern
   - `ClosePositionAsync(positionId, quantity)`
     - `DELETE /trade/positions/{positionId}` mit qty
   - Mapping orderId → positionId über ordersHistory

3. **Trade-Logging in SQLite**
   - Jeder Trade (auch abgelehnte) wird gespeichert
   - Claude-Reasoning und Confidence festhalten
   - Status: Pending → Executed/Failed

4. **Automatischer Stop-Loss**
   - Periodisch offene Positionen prüfen
   - Bei Verlust > Schwelle: Position automatisch schließen
   - Log-Eintrag + Trade-Eintrag erstellen

**Akzeptanzkriterium:** Ein kompletter Zyklus: Claude empfiehlt BUY EURUSD → RiskManager genehmigt → Order wird auf TradeLocker Demo platziert → Trade in DB geloggt.

---

### Phase 5: TradingEngine – Hauptloop (Tag 10-11)

**Ziel:** Der BackgroundService läuft dauerhaft und führt Trading-Zyklen aus.

**Aufgaben:**

1. **TradingEngine als BackgroundService**
   - `ExecuteAsync` Loop mit konfigurierbarem Intervall
   - Pause/Resume Steuerung von außen
   - Fehlerbehandlung: Einzelne Fehler stoppen nicht den Loop

2. **Trading-Zyklus implementieren**
   ```
   Für jedes Symbol in der Watchlist:
     1. Aktuelle Kurse + Historie von TradeLocker holen
     2. Offene Position für dieses Symbol prüfen
     3. Kontostand + Equity abrufen
     4. Alles an Claude schicken → Empfehlung erhalten
     5. RiskManager.ValidateTradeAsync()
     6. Bei Genehmigung: Order ausführen
     7. Ergebnis in DB loggen
     Pause (2-3 Sekunden Rate Limiting)
   ```

3. **Token-Refresh im Hintergrund**
   - JWT Token hat begrenzte Lebensdauer
   - Timer oder DelegatingHandler für automatisches Refresh
   - Bei Auth-Fehler: Reconnect-Logik

4. **Watchlist-Konfiguration**
   ```json
   "TradingStrategy": {
     "WatchList": ["EURUSD", "GBPUSD", "USDJPY", "XAUUSD", "US100"],
     "DefaultQuantity": 0.01,
     "Resolutions": ["1D", "4H", "1H"]
   }
   ```

**Akzeptanzkriterium:** Engine läuft 1 Stunde auf Demo, analysiert alle Symbole, platziert mindestens einen Trade, keine Crashes.

---

### Phase 6: Web Dashboard (Tag 12-14)

**Ziel:** Übersichtliches Dashboard zur Überwachung und Steuerung.

**Aufgaben:**

1. **Dashboard-Seite (Index)**
   - Portfolio-Wert, Equity, freie Margin
   - Tages-PnL (farblich grün/rot)
   - Status-Badges: Engine läuft, TradeLocker verbunden, Kill Switch
   - Offene Positionen mit unrealisiertem P&L
   - Letzte 20 Trades mit Claude-Reasoning
   - Log-Einträge (letzte 30)

2. **Steuerung via Minimal API**
   ```
   POST /api/engine/pause
   POST /api/engine/resume
   POST /api/killswitch/activate
   POST /api/killswitch/reset
   GET  /api/status
   ```

3. **Trade-Historie Seite**
   - Alle Trades mit Filtern (Datum, Symbol, Status)
   - Claude-Begründung ausklappbar
   - Export als CSV

4. **Einstellungen Seite**
   - Watchlist bearbeiten
   - Risk-Parameter anpassen (mit Warnung bei Änderungen)
   - Engine Intervall ändern
   - API-Keys Anzeige (maskiert)

5. **Auto-Refresh**
   - Dashboard aktualisiert sich alle 30 Sekunden
   - Oder: SignalR für Echtzeit-Updates (optional, Phase 8)

**Akzeptanzkriterium:** Dashboard zeigt live Daten, Engine kann gestoppt/gestartet werden, Kill Switch funktioniert.

---

### Phase 7: VPS Deployment (Tag 15-16)

**Ziel:** App läuft stabil als Service auf einem Linux-VPS.

**Aufgaben:**

1. **VPS einrichten (z.B. Hetzner CX22)**
   ```bash
   # .NET 8 Runtime installieren
   sudo apt-get update
   sudo apt-get install -y dotnet-sdk-8.0

   # App publishen (lokal)
   dotnet publish -c Release -o ./publish

   # Auf VPS hochladen
   scp -r ./publish user@vps:/opt/claude-trading-bot/
   ```

2. **Systemd Service erstellen**
   ```ini
   # /etc/systemd/system/claude-trading-bot.service
   [Unit]
   Description=Claude Trading Bot
   After=network.target

   [Service]
   WorkingDirectory=/opt/claude-trading-bot
   ExecStart=/usr/bin/dotnet ClaudeTradingBot.dll
   Restart=always
   RestartSec=10
   Environment=ASPNETCORE_ENVIRONMENT=Production
   Environment=ASPNETCORE_URLS=http://0.0.0.0:5000

   [Install]
   WantedBy=multi-user.target
   ```

3. **Reverse Proxy (Nginx/Caddy)**
   - HTTPS via Let's Encrypt
   - Basic Auth oder Token-Auth vor dem Dashboard
   - Rate Limiting auf API-Endpoints

4. **Monitoring**
   - Serilog File-Sink: `/var/log/claude-trading-bot/`
   - Log-Rotation konfigurieren
   - Optional: Health-Check Endpoint + UptimeRobot

5. **Sicherheit**
   - API-Keys als Environment-Variablen, nicht in appsettings
   - Firewall: nur Port 443 (HTTPS) offen
   - Dashboard hinter Auth schützen
   - SQLite-DB Backup-Cronjob

**Akzeptanzkriterium:** App startet automatisch nach VPS-Reboot, Dashboard erreichbar via HTTPS, Logs werden geschrieben.

---

### Phase 8: Optimierungen (Fortlaufend)

**Optionale Verbesserungen nach dem Basis-System:**

1. **Bessere Claude-Prompts**
   - Technische Indikatoren vorberechnen (SMA, EMA, RSI, MACD)
   - Mehrere Zeitrahmen analysieren
   - News-Sentiment einbauen (Web Search via Claude)

2. **SignalR für Echtzeit-Dashboard**
   - Live-Updates ohne Polling
   - Trade-Notifications im Browser

3. **Performance-Tracking**
   - Sharpe Ratio, Max Drawdown, Win Rate berechnen
   - Equity-Curve Chart auf dem Dashboard
   - Vergleich: Bot vs. Buy-and-Hold

4. **Multi-Account Support**
   - Mehrere TradeLocker-Konten gleichzeitig
   - Pro Konto eigene Strategie/Watchlist

5. **Telegram/Discord Notifications**
   - Trade-Alerts per Bot
   - Kill-Switch-Alarm
   - Tagesreport

6. **Backtesting-Modul**
   - Historische Daten speichern
   - Claude-Entscheidungen gegen vergangene Daten testen

---

## Wichtige Links

- **TradeLocker API Docs:** https://public-api.tradelocker.com/
- **TradeLocker Recipes:** https://public-api.tradelocker.com/recipes
- **Anthropic API Docs:** https://docs.anthropic.com/
- **Claude Model:** claude-sonnet-4-20250514
- **Bestehender Code:** Das heruntergeladene ZIP enthält bereits Models, ClaudeService, RiskManager, TradingEngine und Dashboard – alles muss auf TradeLocker umgebaut werden

---

## Cursor-Workflow Tipps

1. **Phase für Phase abarbeiten** – Jede Phase hat ein klares Akzeptanzkriterium
2. **Cursor Composer nutzen** – Gib ihm diesen Plan als Kontext und sage z.B.: "Implementiere Phase 2: TradeLocker Marktdaten. Hier ist die API-Doku: [Link]"
3. **Tests schreiben** – Besonders für TradeLockerService und RiskManager
4. **Demo zuerst** – Immer `demo.tradelocker.com` bis alles stabil läuft
5. **Git nutzen** – Commit nach jeder Phase

---

## Risiko-Hinweise

⚠️ **Automatisiertes Trading birgt erhebliche finanzielle Risiken.**
⚠️ **Teste IMMER zuerst ausgiebig auf dem Demo-Konto.**
⚠️ **Claude ist kein Finanzberater – die KI kann falsch liegen.**
⚠️ **Kill Switch und Stop-Loss Mechanismen sind nicht optional.**
⚠️ **Forex/CFD-Trading mit Hebel kann zu Verlusten über die Einlage hinaus führen.**
