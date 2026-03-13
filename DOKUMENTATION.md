# Claude Trading Bot - Technische Dokumentation

## Inhaltsverzeichnis

1. [Systemuebersicht](#1-systemuebersicht)
2. [Architektur](#2-architektur)
3. [Trading-Zyklus im Detail](#3-trading-zyklus-im-detail)
4. [Entscheidungslogik (LLM-Analyse)](#4-entscheidungslogik-llm-analyse)
5. [Risikomanagement](#5-risikomanagement)
6. [Order-Ausfuehrung](#6-order-ausfuehrung)
7. [Datenfluss-Diagramme](#7-datenfluss-diagramme)
8. [Konfiguration](#8-konfiguration)
9. [Dashboard](#9-dashboard)

---

## 1. Systemuebersicht

Der Claude Trading Bot ist eine automatisierte Trading-Anwendung, die:

- Forex/CFD-Maerkte ueber die **TradeLocker API** handelt
- Eine **KI (LLM)** fuer Marktanalyse und Handelsentscheidungen nutzt
- Ein **Risikomanagement-System** zur Absicherung implementiert
- Ein **Web-Dashboard** zur Ueberwachung und Steuerung bietet

### Tech-Stack

| Komponente | Technologie |
|------------|-------------|
| Backend | ASP.NET / C# / Razor Pages |
| Broker | TradeLocker REST API (Demo/Live) |
| KI-Analyse | Anthropic Claude, Google Gemini, oder OpenAI-kompatibel (Ollama) |
| Datenbank | SQLite + Entity Framework Core |
| Logging | Serilog (Konsole + Datei) |

---

## 2. Architektur

```
+------------------------------------------------------------------+
|                    ASP.NET Web Dashboard                          |
|       Dashboard (/)    Trades (/Trades)    Settings (/Settings)  |
|       + Minimal API Endpoints (/api/...)                         |
+----------------------------------+-------------------------------+
                                   |
+----------------------------------v-------------------------------+
|                  TradingEngine (BackgroundService)                |
|                                                                  |
|    Laeuft als Endlosschleife im Hintergrund.                     |
|    Analysiert alle X Minuten jedes Symbol der Watchlist.          |
|                                                                  |
|  +----------------+  +---------------------+  +----------------+ |
|  | LLM Service    |  | TradeLockerService  |  | RiskManager    | |
|  | (Analyse)      |  | (Broker API)        |  | (Validierung)  | |
|  |                |  |                     |  |                | |
|  | - Claude       |  | - Auth/JWT          |  | - Kill Switch  | |
|  | - Gemini       |  | - Kurse/Candles     |  | - Stop-Loss    | |
|  | - Ollama       |  | - Orders            |  | - Tagesverlust | |
|  +----------------+  | - Positionen        |  | - Positionsmax | |
|                       +---------------------+  +----------------+ |
+----------------------------------+-------------------------------+
                                   |
                    +--------------v--------------+
                    |     SQLite Datenbank         |
                    |  Trades, Positionen, PnL,    |
                    |  Logs                        |
                    +------------------------------+
```

### Komponenten

| Service | Datei | Aufgabe |
|---------|-------|---------|
| `TradingEngine` | `Services/TradingEngine.cs` | BackgroundService, Hauptloop, orchestriert den gesamten Trading-Zyklus |
| `LlmProviderResolver` | `Services/LlmProviderResolver.cs` | Waehlt zur Laufzeit den LLM-Provider (Claude/Gemini/Ollama) |
| `ClaudeService` | `Services/ClaudeService.cs` | Anthropic Claude API Client |
| `GeminiClaudeService` | `Services/GeminiClaudeService.cs` | Google Gemini API Client (Free Tier) |
| `OpenAICompatibleClaudeService` | `Services/OpenAICompatibleClaudeService.cs` | Ollama / LM Studio / OpenRouter Client |
| `ClaudePromptBuilder` | `Services/ClaudePromptBuilder.cs` | Baut System- und User-Prompt fuer die KI |
| `TradeLockerService` | `Services/TradeLockerService.cs` | TradeLocker REST API (Auth, Orders, Positionen, Kurse) |
| `RiskManager` | `Services/RiskManager.cs` | Validierung, Stop-Loss, Kill Switch, Tagesverlust |

---

## 3. Trading-Zyklus im Detail

### Hauptloop (TradingEngine.ExecuteAsync)

```
                     +------------------+
                     |   Engine Start   |
                     +--------+---------+
                              |
                     +--------v---------+
                     | DB initialisieren |
                     | Broker verbinden  |
                     +--------+---------+
                              |
                  +-----------v-----------+
          +------>|   Pause angefordert?  |
          |       |   Kill Switch aktiv?  |
          |       +-----------+-----------+
          |              |           |
          |             JA          NEIN
          |              |           |
          |    +---------v----+ +----v------------------+
          |    | 10s warten,  | | RunTradingCycleAsync  |
          |    | Engine=OFF   | | (alle Symbole)        |
          |    +---------+----+ +----+------------------+
          |              |           |
          |              |    +------v------------------+
          |              |    | CheckStopLossesAsync    |
          |              |    | (offene Pos. pruefen)   |
          |              |    +------+------------------+
          |              |           |
          |              |    +------v------------------+
          |              |    | RecordDailyPnLAsync     |
          |              |    | (Tages-PnL speichern)   |
          |              |    +------+------------------+
          |              |           |
          |       +------v-----------v-----+
          +-------+ Warte X Minuten        |
                  | (TradingIntervalMinutes)|
                  +------------------------+
```

### Pro Symbol (AnalyzeAndTradeAsync)

Fuer jedes Symbol in der Watchlist wird folgender Ablauf durchlaufen:

```
+------------------------------------------------------+
|              Fuer jedes Symbol der Watchlist           |
+------------------------------------------------------+
                        |
          +-------------v--------------+
          | 1. Marktdaten sammeln      |
          |    - Aktueller Preis (Mid) |
          |    - Bid / Ask             |
          |    - Candles 1D (30 Tage)  |
          |    - Candles 4H (30 Stueck)|
          |    - Candles 1H (30 Stueck)|
          |    - Letzte 20 Preise      |
          +-------------+--------------+
                        |
          +-------------v--------------+
          | 2. Portfolio-Kontext       |
          |    - Verfuegbares Kapital  |
          |    - Portfolio-Gesamtwert  |
          |    - Bestehende Position?  |
          +-------------+--------------+
                        |
          +-------------v--------------+
          | 3. LLM-Analyse             |
          |    System-Prompt +         |
          |    User-Prompt mit allen   |
          |    gesammelten Daten       |
          |                            |
          |    Ergebnis: JSON mit      |
          |    action, quantity,       |
          |    confidence, reasoning,  |
          |    stopLoss, takeProfit    |
          +-------------+--------------+
                        |
          +-------------v--------------+
          | 4. RiskManager Validierung |
          |    (siehe Abschnitt 5)     |
          +------+----------+---------+
                 |          |
              ABGELEHNT   GENEHMIGT
                 |          |
    +------------v---+  +---v-----------------+
    | Trade mit      |  | 5. Order ausfuehren |
    | Status=Failed  |  |    an TradeLocker    |
    | in DB speichern|  +---+-----------------+
    +------------+---+      |
                 |     +----v-----------------+
                 |     | 6. Ergebnis loggen   |
                 |     |    Trade in DB        |
                 |     |    TradingLog in DB   |
                 +---->+-----+----------------+
                             |
                   +---------v---------+
                   | 2 Sekunden Pause  |
                   | (Rate Limiting)   |
                   +-------------------+
                             |
                     [Naechstes Symbol]
```

---

## 4. Entscheidungslogik (LLM-Analyse)

### LLM-Provider Auswahl

Konfiguriert ueber `appsettings.json` > `Llm:Provider`:

| Provider | Service | Beschreibung |
|----------|---------|--------------|
| `Anthropic` | `ClaudeService` | Claude API (kostenpflichtig, hohe Qualitaet) |
| `Gemini` | `GeminiClaudeService` | Google Gemini Free Tier (kostenlos, Rate Limits) |
| `OpenAICompatible` | `OpenAICompatibleClaudeService` | Ollama/LM Studio (lokal, kostenlos) |

### System-Prompt (Rolle der KI)

Die KI erhaelt einen festen System-Prompt, der sie als **quantitativen Trading-Analysten** fuer Forex/CFDs definiert. Die KI muss IMMER ein JSON-Objekt zurueckgeben:

```json
{
  "symbol": "EURUSD",
  "action": "buy | sell | hold",
  "quantity": 0.01,
  "confidence": 0.82,
  "reasoning": "Begruendung...",
  "stopLossPrice": 1.0800,
  "takeProfitPrice": 1.1200
}
```

### Daten die der KI uebergeben werden

| Datenfeld | Quelle | Beschreibung |
|-----------|--------|--------------|
| Symbol | Watchlist | z.B. EURUSD, XAUUSD |
| Aktueller Preis | TradeLocker Quotes | Mid-Preis |
| Bid / Ask | TradeLocker Quotes | Spread-Information |
| Tagesveraenderung | Berechnet | Prozentuale Aenderung gegenueber Vortag |
| Letzte 20 Kurse (1H) | TradeLocker History | Kurzfristiger Trend |
| Candles 1D (30 Tage) | TradeLocker History | Langfristiger Trend (Close-Preise) |
| Candles 4H (30 Stueck) | TradeLocker History | Mittelfristiger Trend |
| Candles 1H (30 Stueck) | TradeLocker History | Kurzfristiger Trend |
| Verfuegbares Kapital | TradeLocker Account | Free Margin |
| Portfolio-Wert | TradeLocker Account | Equity |
| Bestehende Position | TradeLocker Positions | Lots, Avg-Preis, unrealisierter PnL |

### Entscheidungsmoeglichkeiten der KI

```
                  +-------------------+
                  |   LLM analysiert  |
                  |   alle Daten      |
                  +---+-----+-----+--+
                      |     |     |
                +-----v-+ +-v---+ +v-------+
                | BUY   | |HOLD| | SELL   |
                +---+---+ +--+-+ +---+----+
                    |        |       |
                    v        v       v
              Neue Position  Nichts  Position
              eroeffnen      tun     schliessen /
              (Long)                 Short eroeffnen
```

**BUY (Kaufen):**
- KI sieht bullisches Signal (Aufwaertstrend, Unterstuetzung, Umkehrformation)
- Gibt Lot-Groesse, Stop-Loss und Take-Profit vor
- Wird vom RiskManager validiert

**SELL (Verkaufen):**
- KI sieht baerisches Signal oder will bestehende Long-Position schliessen
- Bei bestehender Position: Position wird geschlossen
- Ohne Position: Short-Position wird eroeffnet (wenn vom Broker unterstuetzt)

**HOLD (Halten):**
- KI sieht kein klares Signal
- Keine Aktion, wird nur geloggt
- Kein Eintrag in der Trades-Tabelle

### Confidence-Wert

Die KI gibt einen **Confidence-Wert zwischen 0.0 und 1.0** zurueck:

```
0.0 -------- 0.65 ---------- 0.80 ---------- 1.0
     |          |                |              |
  Sehr unsicher |           Starkes Signal   Absolut sicher
                |
        MinConfidence (Default)
        Trades unter diesem Wert
        werden abgelehnt
```

---

## 5. Risikomanagement

### Validierungskette (RiskManager.ValidateTradeAsync)

Jeder Trade durchlaeuft eine mehrstufige Pruefung. Schlaegt **eine** Pruefung fehl, wird der Trade abgelehnt.

```
Eingehende Empfehlung (BUY/SELL)
              |
     +--------v---------+
     | 1. Kill Switch    |     Kill Switch aktiv?
     |    aktiv?         +---> JA: Trade ABGELEHNT
     +--------+---------+
              | NEIN
     +--------v---------+
     | 2. Ist es ein     |     action == "hold"?
     |    HOLD?          +---> JA: Durchlassen (kein Trade noetig)
     +--------+---------+
              | NEIN
     +--------v---------+
     | 3. Confidence     |     confidence < MinConfidence (0.65)?
     |    ausreichend?   +---> JA: Trade UEBERSPRUNGEN
     +--------+---------+
              | NEIN
     +--------v---------+
     | 4. Positions-     |     Notional Value > MaxPositionSizePercent
     |    groesse OK?    |     des Portfolios?
     |                   +---> JA: Trade ABGELEHNT
     +--------+---------+
              | NEIN
     +--------v---------+
     | 5. Max offene     |     Anzahl Positionen >= MaxOpenPositions
     |    Positionen?    |     UND neues Symbol?
     |    (nur bei BUY)  +---> JA: Trade ABGELEHNT
     +--------+---------+
              | NEIN
     +--------v---------+
     | 6. Tagesverlust   |     Portfolioverlust > MaxDailyLossPercent
     |    ueberschritten?|     ODER > MaxDailyLossAbsolute?
     |                   +---> JA: KILL SWITCH AKTIVIERT
     +--------+---------+          + Trade ABGELEHNT
              | NEIN
              v
     Trade GENEHMIGT --> Weiter zur Order-Ausfuehrung
```

### Positionsgroessen-Berechnung

```
Notional Value = Lots x Standard-Lot-Groesse (100.000) x Aktueller Preis

Beispiel: 0.01 Lots EURUSD @ 1.0950
  = 0.01 x 100.000 x 1.0950
  = 1.095 USD

Position-Prozent = (Notional Value / Portfolio-Wert) x 100

Bei Portfolio-Wert 25.000 USD:
  = (1.095 / 25.000) x 100 = 4.38%
  < MaxPositionSizePercent (10%) --> OK
```

### Automatischer Stop-Loss (CheckStopLossesAsync)

Wird nach jedem Trading-Zyklus ausgefuehrt:

```
Fuer jede offene Position:
              |
     +--------v---------+
     | Aktuellen Preis   |
     | vom Broker holen  |
     +--------+---------+
              |
     +--------v-------------------+
     | Verlust berechnen:         |
     | (AvgPreis - AktuellerPreis)|
     | / AvgPreis x 100           |
     +--------+-------------------+
              |
     +--------v---------+
     | Verlust >=        |     JA: Position SCHLIESSEN
     | StopLossPercent?  +-------> Trade in DB loggen
     +--------+---------+         Log-Eintrag erstellen
              |
              | NEIN
              v
         [Naechste Position]
```

### Kill Switch

Der Kill Switch ist ein Notfall-Mechanismus:

```
+-------------------------------------------+
|            Kill Switch                     |
+-------------------------------------------+
| Aktivierung:                              |
|   - Automatisch: Tagesverlust ueberschritten|
|   - Manuell: Dashboard-Button             |
|                                           |
| Wirkung:                                  |
|   - Engine pausiert (kein Trading)        |
|   - Alle neuen Trades werden abgelehnt   |
|   - Bestehende Positionen bleiben offen   |
|                                           |
| Reset:                                    |
|   - Nur manuell ueber Dashboard           |
+-------------------------------------------+
```

---

## 6. Order-Ausfuehrung

### TradeLocker Order-Flow

```
     Trade genehmigt
           |
  +--------v----------+
  | Symbol aufloesen   |     "EURUSD" --> tradableInstrumentId
  | (Instrument-Cache) |
  +---------+----------+
            |
  +---------v----------+
  | Market Order bauen  |
  |  - side: buy/sell   |
  |  - qty: Lots        |
  |  - type: "market"   |
  |  - validity: "IOC"  |
  |  - stopLoss         |
  |  - takeProfit       |
  +---------+----------+
            |
  +---------v-----------+
  | POST /trade/accounts |
  | /{id}/orders         |
  +---------+-----------+
            |
       +----v----+
       | Erfolg? |
       +--+---+--+
          |   |
         JA  NEIN
          |   |
  +-------v-+ +v-----------+
  | Trade:  | | Trade:      |
  | Status= | | Status=     |
  | Executed| | Failed      |
  | OrderId | | ErrorMessage|
  | speichern| | speichern   |
  +---------+ +------------+
```

### Order-Typen

| Typ | Verwendung |
|-----|-----------|
| Market Order (`type: "market"`) | Sofortige Ausfuehrung zum aktuellen Marktpreis |
| Validity: IOC | Immediate-or-Cancel: Sofort ausfuehren oder verwerfen |
| Stop-Loss | Automatischer Verkauf bei Erreichen des SL-Preises (vom Broker verwaltet) |
| Take-Profit | Automatischer Verkauf bei Erreichen des TP-Preises (vom Broker verwaltet) |

### Position schliessen

```
RiskManager/KI empfiehlt SELL fuer bestehende Position
              |
     +--------v-----------+
     | BrokerPositionId    |
     | vorhanden?          |
     +--+----------+------+
        |          |
       JA         NEIN
        |          |
  +-----v------+ +v-----------+
  | DELETE      | | Fallback:  |
  | /trade/     | | Symbol als |
  | positions/  | | Identifier |
  | {posId}     | +-----+------+
  | qty=0       |       |
  | (komplett)  +---+---+
  +-------------+   |
                    v
              Position geschlossen
```

---

## 7. Datenfluss-Diagramme

### Gesamter Datenfluss pro Analyse-Zyklus

```
+-------------------+        +-------------------+
| TradeLocker API   |        | LLM Provider      |
| (Broker)          |        | (Claude/Gemini/   |
|                   |        |  Ollama)           |
+--------+----------+        +--------+----------+
         |                             ^
         | Kurse, Candles,             | System-Prompt +
         | Positionen, Konto           | Marktdaten +
         v                             | Portfolio
+--------+-----------------------------+----------+
|                TradingEngine                     |
|                                                  |
|  1. Marktdaten holen ---------> TradeLocker      |
|  2. Portfolio-Status holen ---> TradeLocker      |
|  3. Analyse anfragen ---------> LLM Provider     |
|  4. Empfehlung validieren ----> RiskManager      |
|  5. Order ausfuehren ---------> TradeLocker      |
|  6. Ergebnis loggen ----------> SQLite DB        |
+--------+-----------------------------------------+
         |
         v
+--------+----------+
| SQLite Datenbank   |
| - Trades           |
| - TradingLogs      |
| - DailyPnL         |
+--------------------+
         |
         v
+--------+----------+
| Web Dashboard      |
| - Liest DB         |
| - Zeigt Daten      |
| - Steuert Engine   |
+--------------------+
```

### Datenbank-Entitaeten

```
+------------------+     +------------------+     +------------------+
| Trade            |     | TradingLog       |     | DailyPnL         |
+------------------+     +------------------+     +------------------+
| Id               |     | Id               |     | Id               |
| Symbol           |     | Level            |     | Date             |
| Action (Buy/Sell)|     | Source           |     | RealizedPnL      |
| Status           |     | Message          |     | UnrealizedPnL    |
| Quantity (Lots)  |     | Details          |     | PortfolioValue   |
| Price            |     | Timestamp        |     | TradeCount       |
| ExecutedPrice    |     +------------------+     +------------------+
| ClaudeReasoning  |
| ClaudeConfidence |
| CreatedAt        |
| ExecutedAt       |
| ErrorMessage     |
| BrokerOrderId    |
| BrokerPositionId |
+------------------+

Trade.Status:
  Pending   --> Order ist raus, warte auf Bestaetigung
  Executed  --> Erfolgreich ausgefuehrt
  Failed    --> Abgelehnt (RiskManager) oder Broker-Fehler
  Cancelled --> Manuell abgebrochen
```

---

## 8. Konfiguration

### Risiko-Parameter (appsettings.json > RiskManagement)

| Parameter | Default | Beschreibung |
|-----------|---------|--------------|
| `MinConfidence` | 0.65 | Minimale KI-Confidence fuer Trade-Ausfuehrung (0-1) |
| `MaxPositionSizePercent` | 10.0 | Max. Anteil am Portfolio pro Trade (%) |
| `MaxDailyLossPercent` | 3.0 | Max. Tagesverlust prozentual (%) |
| `MaxDailyLossAbsolute` | 500 | Max. Tagesverlust absolut ($) |
| `StopLossPercent` | 5.0 | Automatischer Stop-Loss pro Position (%) |
| `MaxOpenPositions` | 10 | Maximale Anzahl gleichzeitig offener Positionen |
| `TradingIntervalMinutes` | 15 | Minuten zwischen Analyse-Zyklen |
| `KillSwitchEnabled` | true | Kill Switch Funktion aktiviert |

### Watchlist (appsettings.json > TradingStrategy)

```json
{
  "TradingStrategy": {
    "WatchList": ["EURUSD", "GBPUSD", "USDJPY", "XAUUSD", "US100"]
  }
}
```

### LLM-Provider Wechsel

```json
{
  "Llm": {
    "Provider": "OpenAICompatible"   // "Anthropic" | "Gemini" | "OpenAICompatible"
  }
}
```

| Provider | Kosten | Latenz | Empfehlung |
|----------|--------|--------|------------|
| Anthropic (Claude) | ~0.003$/Analyse | ~2-4s | Beste Qualitaet |
| Gemini (Free Tier) | Kostenlos | ~1-3s | Guter Kompromiss |
| Ollama (Lokal) | Kostenlos | ~5-15s | Volle Kontrolle, braucht GPU |

---

## 9. Dashboard

### Seitenstruktur

```
+------------------------------------------------------------+
|  Claude Trading Bot                                        |
|  [Dashboard]    [Trades]    [Einstellungen]                |
+------------------------------------------------------------+

Dashboard (/)
  - Status-Badges: Engine, TradeLocker, Kill Switch
  - Stats: Portfolio-Wert, Tages-PnL, Kapital, Positionen, Trades
  - Steuerbuttons: Start, Pause, Kill Switch, Reset
  - Offene Positionen (Tabelle)
  - Letzte 20 Trades
  - Letzte 30 Log-Eintraege
  - Auto-Refresh alle 30 Sekunden

Trades (/Trades)
  - Filter: Datum Von/Bis, Symbol, Status
  - Alle Trades mit aufklappbarer KI-Begruendung
  - CSV-Export (Semicolon-separiert)

Einstellungen (/Settings)
  - Watchlist bearbeiten
  - Risiko-Parameter aendern
  - Trading-Intervall aendern
  - API-Keys anzeigen (maskiert, readonly)
```

### API-Endpoints

| Endpoint | Methode | Beschreibung |
|----------|---------|--------------|
| `/api/engine/pause` | POST | Engine pausieren |
| `/api/engine/resume` | POST | Engine fortsetzen |
| `/api/killswitch/activate` | POST | Kill Switch manuell aktivieren |
| `/api/killswitch/reset` | POST | Kill Switch zuruecksetzen |
| `/api/status` | GET | Engine/Broker/KillSwitch Status als JSON |
| `/api/trades/export` | GET | Trade-Historie als CSV herunterladen |

---

## Zusammenfassung: Wann wird gekauft, verkauft, gehalten?

```
+============================================================+
|                    ENTSCHEIDUNGSMATRIX                       |
+============================================================+

KAUFEN (BUY) wird ausgefuehrt wenn:
  [x] KI empfiehlt "buy"
  [x] Confidence >= MinConfidence (65%)
  [x] Positionsgroesse <= MaxPositionSizePercent (10%)
  [x] Offene Positionen < MaxOpenPositions (10)
  [x] Tagesverlust < Limit
  [x] Kill Switch NICHT aktiv
  --> Market Order an TradeLocker mit Stop-Loss + Take-Profit

VERKAUFEN (SELL) wird ausgefuehrt wenn:
  [x] KI empfiehlt "sell"
  [x] Confidence >= MinConfidence (65%)
  [x] Positionsgroesse <= MaxPositionSizePercent (10%)
  [x] Tagesverlust < Limit
  [x] Kill Switch NICHT aktiv
  --> Position schliessen oder Short eroeffnen

  ODER automatisch durch Stop-Loss:
  [x] Unrealisierter Verlust >= StopLossPercent (5%)
  --> Position wird sofort geschlossen

HALTEN (HOLD) passiert wenn:
  - KI empfiehlt "hold" (kein klares Signal)
  - KI-Confidence < MinConfidence (zu unsicher)
  - RiskManager lehnt Trade ab (Limits erreicht)
  - Kill Switch ist aktiv
  - Engine ist pausiert
  --> Keine Aktion, nur Log-Eintrag

+============================================================+
```
