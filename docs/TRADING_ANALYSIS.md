# Trading-Analyse: ClaudeTradingBot

**Ziel:** Gewinnmaximierung durch Identifikation von Problemen im automatisierten Trading, fehlenden Einstellungen und Verbesserungspotenzial der LLM-Nutzung.

---

## 1. Kritische Probleme beim automatisierten Trading

### 1.1 Keine Berücksichtigung von Spread/Slippage bei der Ausführung

**Problem:** Market-Orders werden mit dem **Mid-Preis** (Bid+Ask)/2 bewertet, die tatsächliche Ausführung erfolgt aber:
- **Buy:** am **Ask** (höher als Mid)
- **Sell:** am **Bid** (niedriger als Mid)

**Betroffene Stellen:**
- `TradingEngine.cs:674` – `trade.ExecutedPrice = currentPrice` (Mid statt Ask/Bid)
- `RiskManager.cs:331` – Stop-Loss-Close mit `currentPrice` statt tatsächlichem Fill
- `CalculatePositionSize` – nutzt `currentPrice`; bei Buy sollte der erwartete Einstieg = Ask sein

**Auswirkung:** 
- PnL-Berechnung ist systematisch zu optimistisch (ca. 0.5–2 Pips pro Trade je nach Symbol)
- Risk-basiertes Position Sizing unterschätzt das tatsächliche Risiko
- Backtest ignoriert Spread komplett (`Bid = Ask = Close`)

**Empfehlung:**
- Bei Buy: `executionPrice = ask`, bei Sell: `executionPrice = bid`
- Im Backtest: Spread simulieren (z.B. 1–2 Pips je nach Symbol)
- Neue Einstellung: `SimulatedSpreadPips` für Backtests

---

### 1.2 Daily-Loss-Logik potenziell fehlerhaft

**Problem:** `IsDailyLossExceededAsync` vergleicht `yesterdayRecord.PortfolioValue` mit `currentValue`.

- Wenn **kein** Eintrag von gestern existiert → `return false` → Daily Loss wird nie ausgelöst
- „Daily Loss“ wird als Verlust **seit gestern** interpretiert, nicht als Verlust **seit Tagesbeginn**

**Betroffene Stelle:** `RiskManager.cs:318–346`

**Empfehlung:**
- Referenzwert: Portfolio-Wert **zu Tagesbeginn** (z.B. erster `DailyPnL`-Eintrag des Tages oder separater `StartOfDayEquity`)
- Fallback: Wenn kein Eintrag von gestern, z.B. aktuellen Wert als Referenz setzen (kein Verlust an Tag 1)

---

### 1.3 MaxOpenPositions prüft nur bei Buy

**Problem:** `ValidateTradeAsync` prüft `MaxOpenPositions` nur für `normalizedAction == "buy"`:

```csharp
if (normalizedAction.Equals("buy", StringComparison.OrdinalIgnoreCase))
{
    if (positions.Count >= Settings.MaxOpenPositions && !positions.Any(p => p.Symbol == rec.Symbol))
```

Sell-Positionen zählen mit, werden aber nicht separat limitiert. Das ist konsistent (eine Position = ein Slot), aber: **Limit/Stop-Orders** (`buy_limit`, `sell_stop` etc.) werden wie Market-Orders gezählt – Pending Orders könnten das Limit umgehen, wenn sie später ausgeführt werden.

**Empfehlung:** Prüfen, ob Pending Orders in `positions.Count` enthalten sind oder ob ein separates Limit für offene + pending Orders nötig ist.

---

### 1.4 Gegenrichtung: Sofortiges Schließen ohne Bestätigung

**Problem:** Wenn das LLM z.B. Sell empfiehlt und eine Buy-Position offen ist, wird die Position **sofort** geschlossen – unabhängig von der Höhe von Confidence oder vom Marktkontext.

**Betroffene Stelle:** `TradingEngine.cs:358–406`

**Risiko:** Bei wechselnden Signalen (z.B. Rauschen) können Positionen unnötig oft geschlossen und neu eröffnet werden → erhöhte Kosten durch Spread.

**Empfehlung:**
- Mindest-Confidence für Gegenrichtungs-Close (z.B. ≥ MinConfidence)
- Optional: Bestätigung über 2 aufeinanderfolgende Zyklen mit gleicher Gegenrichtung

---

### 1.5 Backtest: Kein Spread, unrealistische Fills

**Problem:** Im Backtest (`BacktestEngine.cs:334–336`):
- `Bid = Ask = currentCandle.Close` → kein Spread
- SL/TP werden exakt am Candle High/Low getroffen (realistisch, aber optimistisch bei engen SL)

**Empfehlung:**
- Spread im Backtest: z.B. `Ask = Close + spread/2`, `Bid = Close - spread/2`
- Slippage: z.B. 0.5–1 Pip bei Market-Orders
- Neue Einstellungen: `BacktestSpreadPips`, `BacktestSlippagePips`

---

### 1.6 Risk-basiertes Sizing: LotSize-Handling für CFDs

**Problem:** `GetLotSize` in `RiskManager` und `PipCalculator.GetPipValuePerLot` haben unterschiedliche Logik für Indizes/CFDs. Bei z.B. US100 (1 Pip = 1 Punkt) kann der Pip-Wert pro Lot abweichen.

**Empfehlung:** Lot-Größen und Pip-Werte für alle gehandelten Instrumente (Forex, Gold, Indizes, Öl) dokumentieren und testen.

---

## 2. Fehlende Einstellmöglichkeiten

### 2.1 Hardcodierte Werte, die konfigurierbar sein sollten

| Ort | Aktueller Wert | Vorschlag |
|-----|----------------|-----------|
| `TradingEngine.cs:262` | 2 Sekunden Pause zwischen Symbolen | `AnalysisDelaySeconds` |
| `TradingEngine.cs:310` | 210 Candles für Indikatoren | `IndicatorCandleCount` |
| `TradingEngine.cs:300` | 20 Recent Prices | `RecentPricesCount` |
| `TradingEngine.cs:616` | 10 Recent Trade Results | `FeedbackLoopTradeCount` |
| **`TradingEngine.cs:623`** | **50 Pips Default-SL** | **`DefaultStopLossPips`** |
| **`TradingEngine.cs:637`** | **1.5× SL für Default-TP** | **`DefaultTakeProfitRatio`** (z.B. 1.5) |
| `TradingEngine.cs:400` | 0.8 Confidence für Grid-Deaktivierung | `GridDeactivationMinConfidence` |
| `RiskManager.cs:395` | 0.3 Korrelations-Schwelle | `CorrelationThreshold` |
| `RiskManager.cs:437` | 0.5 ATR-% für dynamische Confidence | Bereits `ConfidenceAtrFactor` |
| `RiskManager.cs:455` | 0.5 Win-Rate-Schwelle | `ConfidenceWinRateThreshold` |
| `MarketHoursService` | Statische Feiertage | `Holidays` (Liste/Config) |
| `TradingSessionService` | Statische Sessions | `SessionDefinitions` (Config) |

### 2.2 Weitere sinnvolle Einstellungen

| Einstellung | Beschreibung | Default |
|------------|--------------|---------|
| `MinRiskRewardRatio` | Mindest-R/R (TP-Distanz / SL-Distanz) für Trade | 1.0 |
| `MaxSlippagePips` | Max. akzeptierter Slippage bei Ausführung | 2 |
| `RequireSlTpFromLlm` | Trade ablehnen, wenn LLM keinen SL/TP liefert | false |
| `LlmRetryOnNull` | Bei null-Response erneut anfragen | false |
| `BacktestSpreadPips` | Spread-Simulation im Backtest | 1.0 |
| `BacktestSlippagePips` | Slippage-Simulation im Backtest | 0.5 |
| `StartOfDayEquitySource` | "FirstRecord" / "PreviousClose" / "Manual" | "PreviousClose" |

---

## 3. LLM-Nutzung verbessern

### 3.1 Prompt-Optimierung

**Aktuell:**
- System-Prompt enthält viele Regeln, aber wenig explizite Gewinnmaximierung
- Keine klare Anweisung zu Risk/Reward
- Keine explizite Aufforderung, Verluste aus dem Feedback-Loop zu vermeiden

**Vorschläge:**

1. **Risk/Reward explizit machen:**
   ```
   - Empfiehl nur Trades mit mindestens 1:1.5 Risk/Reward (TP-Distanz >= 1.5× SL-Distanz).
   - Bei unsicheren Setups: hold oder kleinere quantity.
   ```

2. **Feedback-Loop stärker nutzen:**
   ```
   - Analysiere die letzten Trades: Welche Setups (setupType) hatten negative PnL?
   - Vermeide ähnliche Setups oder reduziere quantity/confidence.
   ```

3. **Kontext für Volatilität:**
   - ATR als % des Preises im Prompt hervorheben
   - Bei hoher Volatilität: kleinere Lots oder höhere Confidence verlangen

4. **Setup-spezifische Regeln:**
   - Im Strategy-Prompt: „Bei EMA-Cross nur kaufen, wenn RSI < 60“
   - „Bei RSI-Oversold: TP mindestens 2× ATR“

### 3.2 Fehlende Kontext-Daten für das LLM

| Fehlend | Nutzen |
|---------|--------|
| Höherer Timeframe-Trend (z.B. 1W) | Bessere Trendfilterung |
| Unterstützungen/Resistenzen | Präzisere SL/TP |
| Volumen (wo verfügbar) | Bestätigung von Breakouts |
| Economic Calendar (High-Impact) | Vermeidung von News-Trades |
| Korrelation zu anderen Positionen | Vermeidung von Übergewichtung |

**Hinweis:** Economic Calendar wird bereits für den News-Filter genutzt, aber die konkreten Events werden dem LLM nicht übergeben.

### 3.3 Strukturierte Ausgabe erweitern

**Aktuell:** `action`, `quantity`, `confidence`, `reasoning`, `stopLossPrice`, `takeProfitPrice`, `entryPrice`, `setupType`, `gridCenterPrice`

**Vorschläge:**
- `riskRewardRatio`: vom LLM berechnet, zur Validierung
- `expectedMovePips`: erwartete Bewegung für Sizing-Check
- `invalidationLevel`: Preis, bei dem das Setup ungültig wird (für dynamischen SL)

### 3.4 Retry und Fallback bei LLM-Fehlern

**Aktuell:** Bei `recommendation == null` → Abbruch, kein Retry.

**Empfehlung:**
- 1–2 Retries bei Timeout/API-Fehler
- Bei `hold`-ähnlicher Unsicherheit: explizit `hold` zurückgeben statt `null`
- Optional: Fallback-Strategie (z.B. EMA-Cross) wenn LLM dauerhaft ausfällt

### 3.5 Batch-Analyse für mehrere Symbole

**Aktuell:** Pro Symbol ein LLM-Call → bei 10 Symbolen 10 Calls pro Zyklus.

**Idee:** Ein Call mit allen Symbolen, strukturierte JSON-Array-Antwort. Spart Kosten und Token, erlaubt korrelationsbewusste Entscheidungen.

---

## 4. Gewinnmaximierung: Priorisierte Maßnahmen

### Priorität 1 (kurzfristig, hoher Impact)

1. **Spread bei Ausführung berücksichtigen**  
   - Buy: Ask, Sell: Bid für `ExecutedPrice` und Sizing  
   - Deutlich realistischere PnL- und Risikoberechnung  

2. **Default-SL/TP konfigurierbar machen**  
   - `DefaultStopLossPips`, `DefaultTakeProfitRatio`  
   - Ermöglicht symbol- und strategieabhängige Anpassung  

3. **Prompt: Risk/Reward erzwingen**  
   - Mindest-R/R im System-Prompt  
   - Reduziert schlechte Setups mit ungünstigem R/R  

### Priorität 2 (mittelfristig)

4. **Backtest mit Spread/Slippage**  
   - Realistischere Backtest-Ergebnisse  

5. **Daily-Loss-Referenz korrigieren**  
   - Start-of-Day-Equity statt „gestern“  

6. **Fehlende Einstellungen ergänzen**  
   - `AnalysisDelaySeconds`, `FeedbackLoopTradeCount`, `GridDeactivationMinConfidence`  

### Priorität 3 (langfristig)

7. **LLM-Kontext erweitern**  
   - Economic Calendar, S/R, Korrelationen  

8. **Batch-LLM-Analyse**  
   - Weniger Calls, bessere Gesamtübersicht  

9. **Gegenrichtungs-Close mit Bestätigung**  
   - Z.B. 2 Zyklen oder Mindest-Confidence  

---

## 5. Zusammenfassung

| Kategorie | Anzahl | Schweregrad |
|-----------|--------|-------------|
| Kritische Trading-Probleme | 6 | Hoch |
| Fehlende Einstellungen | 15+ | Mittel |
| LLM-Verbesserungen | 8 | Mittel–Hoch |

Die wichtigsten Hebel für die Gewinnmaximierung sind:
1. **Realistische Ausführungspreise** (Spread)
2. **Konfigurierbare Default-SL/TP**
3. **Stärkere Risk/Reward-Vorgaben im LLM-Prompt**
4. **Backtest mit Spread/Slippage**

Diese Änderungen verbessern die Profitabilität sowohl durch realistischere Modellierung als auch durch bessere Trade-Selektion.
