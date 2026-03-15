# NeuronTrade — Einstellungen & Risikomanagement

Vollstaendige Dokumentation aller konfigurierbaren Parameter mit Beschreibungen, Formeln und Zusammenhaengen.

---

## Uebersicht: Validierungsreihenfolge

Jeder Trade durchlaeuft diese Pruefungen in genau dieser Reihenfolge. Schlaegt eine fehl, wird der Trade abgelehnt.

```
LLM-Empfehlung
    |
    v
1. Kill Switch aktiv?              → Abgelehnt
2. Action = "hold"?                → Keine Aktion (OK)
3. Confidence >= MinConfidence?    → Abgelehnt wenn zu niedrig
4. Spread <= MaxSpreadPips?        → Abgelehnt wenn zu hoch
5. Positionsgroesse <= MaxPositionSizePercent?  → Abgelehnt wenn zu gross
6. Offene Positionen < MaxOpenPositions?       → Abgelehnt wenn voll
7. Tagesverlust < MaxDailyLoss?    → Kill Switch aktiviert
8. Drawdown < MaxDrawdownPercent?  → Kill Switch aktiviert
9. Wochenverlust < MaxWeeklyLoss?  → Abgelehnt
10. Monatsverlust < MaxMonthlyLoss? → Abgelehnt
11. Korrelierte Exposure < Max?     → Abgelehnt
    |
    v
Trade wird ausgefuehrt
```

---

## 1. Risiko-Parameter

### MinConfidence
| | |
|---|---|
| **Default** | 0.65 (65%) |
| **Bereich** | 0.0 – 1.0 |
| **Beschreibung** | Minimale KI-Confidence damit ein Trade ausgefuehrt wird. Das LLM gibt bei jeder Analyse einen Confidence-Wert zwischen 0 und 1 zurueck. Trades unter diesem Schwellenwert werden abgelehnt. |
| **Empfehlung** | 0.65–0.75. Hoehere Werte = weniger aber qualitativere Trades. |

### RiskPerTradePercent
| | |
|---|---|
| **Default** | 0 (deaktiviert — LLM bestimmt Lotgroesse) |
| **Bereich** | 0.0 – 10.0 |
| **Beschreibung** | Prozent des Portfolios das pro Trade riskiert wird. Bestimmt die Lotgroesse basierend auf der Stop-Loss-Distanz. |
| **Formel** | `Lots = (Portfolio × RiskPerTrade%) / (SL-Distanz-in-Pips × Pip-Wert-pro-Lot)` |
| **Beispiel** | Portfolio: $25.000, Risk: 2%, SL-Distanz: 50 Pips, EURUSD (Pip-Wert: $10/Lot) → `(25.000 × 0.02) / (50 × 10) = 500 / 500 = 1.0 Lots` |
| **Empfehlung** | 1–2% fuer konservativ, 2–3% fuer moderat. 0 = LLM entscheidet (nicht empfohlen). |
| **Relation** | Wenn aktiviert, ueberschreibt die vom LLM vorgeschlagene Lotgroesse. Setzt voraus dass SL vom LLM oder Default gesetzt wird. |

### MaxPositionSizePercent
| | |
|---|---|
| **Default** | 10.0% |
| **Bereich** | 1 – 100 |
| **Beschreibung** | Maximaler Handelswert (Notional Value) eines einzelnen Trades relativ zum Portfolio. Sicherheitsnetz gegen zu grosse Positionen. |
| **Formel** | `Notional = Lots × LotSize × Preis` |
| **Formel** | `PositionPercent = (Notional / PortfolioValue) × 100` |
| **Beispiel** | 0.02 Lots EURUSD @ 1.08: `0.02 × 100.000 × 1.08 = $2.160`. Bei $25.000 Portfolio: `2.160 / 25.000 × 100 = 8.6%` → OK (unter 10%) |
| **Empfehlung** | 5–15%. Niedrig halten bei kleinen Konten. |

**LotSize nach Instrument:**

| Typ | Instrumente | 1 Lot = |
|---|---|---|
| Forex | EURUSD, GBPUSD, etc. | 100.000 Einheiten |
| Gold/Silber | XAUUSD, XAGUSD | 100 Unzen |
| Indizes | US100, US500, DE30 | 1 Kontrakt |
| Oel | XTIUSD, XBRUSD | 1.000 Barrel |

### MaxOpenPositions
| | |
|---|---|
| **Default** | 10 |
| **Bereich** | 1 – unbegrenzt |
| **Beschreibung** | Maximale Anzahl gleichzeitig offener Positionen. Nur bei Buy-Trades geprueft. Sell-Trades (Schliessen) werden nicht blockiert. Ein Trade auf ein Symbol das bereits offen ist, zaehlt nicht als neue Position. |
| **Empfehlung** | 3–10 je nach Kontogroesse. |

### TradingIntervalMinutes
| | |
|---|---|
| **Default** | 15 |
| **Bereich** | 1 – unbegrenzt |
| **Beschreibung** | Wie oft die Engine Marktdaten abruft, das LLM befragt und Trades ausfuehrt. Pro Zyklus werden alle Symbole der Watchlist analysiert. |
| **Empfehlung** | 15–30 fuer aktives Trading. 60 fuer weniger API-Aufrufe. |

### MaxSpreadPips
| | |
|---|---|
| **Default** | 0 (deaktiviert) |
| **Bereich** | 0 – unbegrenzt |
| **Beschreibung** | Maximaler erlaubter Spread in Pips. Trade wird abgelehnt wenn der aktuelle Spread (Ask - Bid) hoeher ist. Schuetzt vor Trades in illiquiden Maerkten (Nacht, News). |
| **Formel** | `SpreadPips = PriceToPips(symbol, Ask - Bid)` |
| **Empfehlung** | 2–3 Pips fuer Forex Majors. 0 = Filter deaktiviert. |

---

## 2. Verlustlimits

### StopLossPercent
| | |
|---|---|
| **Default** | 5.0% |
| **Bereich** | 0 – unbegrenzt |
| **Beschreibung** | Lokaler (software-seitiger) Stop-Loss. Prueft alle offenen Positionen periodisch. Schliesst eine Position wenn der Verlust X% vom Einstiegspreis uebersteigt. Dies ist ein Fallback — der primaere SL ist auf Broker-Seite (stopLossPrice). |
| **Formel** | `LossPercent = |CurrentPrice - EntryPrice| / EntryPrice × 100` |
| **Wichtig** | Dieser SL funktioniert nur wenn die App laeuft. Bei Crash/Disconnect greift nur der Broker-SL. |

### MaxDailyLossPercent
| | |
|---|---|
| **Default** | 3.0% |
| **Bereich** | 0 – unbegrenzt |
| **Beschreibung** | Maximaler Tagesverlust in Prozent. **Aktiviert den Kill Switch** wenn ueberschritten. |
| **Formel** | `DailyLoss = GestrigPortfolioValue - AktuellerPortfolioValue` |
| **Formel** | `DailyLossPercent = DailyLoss / GestrigPortfolioValue × 100` |
| **Verhalten** | Kill Switch wird aktiviert → alle neuen Trades blockiert bis manueller Reset. |

### MaxDailyLossAbsolute
| | |
|---|---|
| **Default** | $500 |
| **Bereich** | 0 – unbegrenzt |
| **Beschreibung** | Maximaler Tagesverlust als absoluter Dollarbetrag. **Aktiviert den Kill Switch** wenn ueberschritten. Wird zusaetzlich zum prozentualen Limit geprueft. |
| **Formel** | `DailyLoss = GestrigPortfolioValue - AktuellerPortfolioValue` |

### MaxWeeklyLossPercent
| | |
|---|---|
| **Default** | 0 (deaktiviert) |
| **Bereich** | 0 – unbegrenzt |
| **Beschreibung** | Maximaler Wochenverlust in Prozent. **Blockiert neue Trades** (aktiviert NICHT den Kill Switch). |
| **Formel** | `WeeklyLoss = PortfolioValue(letzter Tag vor Montag) - AktuellerPortfolioValue` |
| **Empfehlung** | 5–8%. 0 = deaktiviert. |

### MaxMonthlyLossPercent
| | |
|---|---|
| **Default** | 0 (deaktiviert) |
| **Bereich** | 0 – unbegrenzt |
| **Beschreibung** | Maximaler Monatsverlust in Prozent. **Blockiert neue Trades** (aktiviert NICHT den Kill Switch). |
| **Formel** | `MonthlyLoss = PortfolioValue(letzter Tag vor Monatsbeginn) - AktuellerPortfolioValue` |
| **Empfehlung** | 10–15%. 0 = deaktiviert. |

### MaxDrawdownPercent
| | |
|---|---|
| **Default** | 0 (deaktiviert) |
| **Bereich** | 0 – unbegrenzt |
| **Beschreibung** | Maximaler Drawdown vom hoechsten Equity-Wert (Peak). **Aktiviert den Kill Switch**. |
| **Formel** | `PeakEquity = Max(alle bisherigen PortfolioValues)` |
| **Formel** | `Drawdown = PeakEquity - AktuellerPortfolioValue` |
| **Formel** | `DrawdownPercent = Drawdown / PeakEquity × 100` |
| **Beispiel** | Peak: $28.000, Aktuell: $25.200 → Drawdown: 10% |
| **Empfehlung** | 10–20%. 0 = deaktiviert. |

### MaxCorrelatedExposurePercent
| | |
|---|---|
| **Default** | 0 (deaktiviert) |
| **Bereich** | 0 – unbegrenzt |
| **Beschreibung** | Maximale Gesamtexposure zu korrelierten Symbolen. Verhindert z.B. dass EURUSD + GBPUSD (Korrelation 0.85) zusammen zu viel Risiko darstellen. |
| **Formel** | `KorrelierteExposure = NeuerTradeWert + Σ(PositionWert × Korrelation)` fuer alle Positionen mit Korrelation > 0.3 |
| **Formel** | `ExposurePercent = KorrelierteExposure / PortfolioValue × 100` |
| **Beispiel** | Buy 0.02 EURUSD ($2.160) + offene Buy 0.02 GBPUSD ($2.500, Korrelation 0.85): `2.160 + 2.500 × 0.85 = 4.285` → 17.1% |
| **Empfehlung** | 20–30%. 0 = deaktiviert. |

**Eingebaute Korrelationen:**

| Paar A | Paar B | Korrelation |
|---|---|---|
| EURUSD | GBPUSD | +0.85 |
| EURUSD | USDCHF | -0.90 |
| AUDUSD | NZDUSD | +0.90 |
| US100 | US500 | +0.95 |
| XAUUSD | EURUSD | +0.40 |

---

## 3. Gewinnschutz

### TrailingStopPips
| | |
|---|---|
| **Default** | 0 (deaktiviert) |
| **Bereich** | 0 – unbegrenzt |
| **Beschreibung** | Zieht den Stop-Loss automatisch nach wenn die Position im Gewinn ist. Der SL bleibt immer X Pips hinter dem aktuellen Preis. Wird nur aktualisiert wenn der neue SL besser ist als der Einstiegspreis. |
| **Formel (Buy)** | `TrailingSL = AktuellerPreis - (TrailingStopPips × PipSize)` |
| **Formel (Sell)** | `TrailingSL = AktuellerPreis + (TrailingStopPips × PipSize)` |
| **Bedingung** | Greift erst wenn `GewinnPips > TrailingStopPips` UND neuer SL > Einstiegspreis |
| **Empfehlung** | 20–50 Pips. 0 = deaktiviert. |

### BreakevenTriggerPips
| | |
|---|---|
| **Default** | 0 (deaktiviert) |
| **Bereich** | 0 – unbegrenzt |
| **Beschreibung** | Verschiebt den Stop-Loss auf den Einstiegspreis (+1 Pip) sobald die Position X Pips im Gewinn ist. Sichert den Trade gegen Verlust ab. Wird einmalig ausgefuehrt. |
| **Formel (Buy)** | `BreakevenSL = Einstiegspreis + 1 Pip` |
| **Bedingung** | `GewinnPips >= BreakevenTriggerPips` |
| **Empfehlung** | 15–30 Pips. 0 = deaktiviert. |
| **Relation** | Kann zusammen mit TrailingStop verwendet werden. Breakeven wird zuerst geprueft. |

### PartialClosePercent
| | |
|---|---|
| **Default** | 0 (deaktiviert) |
| **Bereich** | 0.0 – 1.0 |
| **Beschreibung** | Anteil der Position der geschlossen wird wenn der Gewinn PartialCloseTriggerPips erreicht. Sichert einen Teil des Gewinns, laesst den Rest weiterlaufen. Wird pro Position nur einmal ausgefuehrt. |
| **Formel** | `CloseQuantity = PositionQuantity × PartialClosePercent` (auf 0.01 gerundet) |
| **Beispiel** | Position: 0.10 Lots, PartialClosePercent: 0.5 → 0.05 Lots werden geschlossen. |
| **Empfehlung** | 0.3–0.5 (30–50%). 0 = deaktiviert. |

### PartialCloseTriggerPips
| | |
|---|---|
| **Default** | 30 |
| **Bereich** | 0 – unbegrenzt |
| **Beschreibung** | Gewinn in Pips ab dem der Partial Close ausgeloest wird. |
| **Empfehlung** | 20–50 Pips. |

---

## 4. Grid-Trading

Grid-Trading platziert automatisch Buy- und Sell-Orders in regelmaessigen Abstaenden um einen Mittelpunkt. Profitiert von Seitwaertsmaerkten.

```
Sell Level +5  ──────  CenterPrice + 5 × Spacing
Sell Level +4  ──────  CenterPrice + 4 × Spacing
Sell Level +3  ──────  CenterPrice + 3 × Spacing
Sell Level +2  ──────  CenterPrice + 2 × Spacing
Sell Level +1  ──────  CenterPrice + 1 × Spacing
═══════════════════  CENTER PRICE
Buy Level -1   ──────  CenterPrice - 1 × Spacing
Buy Level -2   ──────  CenterPrice - 2 × Spacing
Buy Level -3   ──────  CenterPrice - 3 × Spacing
Buy Level -4   ──────  CenterPrice - 4 × Spacing
Buy Level -5   ──────  CenterPrice - 5 × Spacing
```

### GridSpacingPips
| | |
|---|---|
| **Default** | 20 Pips |
| **Beschreibung** | Abstand zwischen benachbarten Grid-Levels. |
| **Formel** | `LevelPreis = CenterPrice ± (LevelIndex × GridSpacingPips × PipSize)` |
| **Beispiel** | EURUSD, Center: 1.1000, Spacing: 20 Pips → Buy-Level-1: 1.0980, Sell-Level+1: 1.1020 |

### GridLevelsAbove / GridLevelsBelow
| | |
|---|---|
| **Default** | 5 / 5 |
| **Beschreibung** | Anzahl Sell-Levels oberhalb und Buy-Levels unterhalb des Centers. Total: Above + Below Levels. |

### LotSizePerLevel
| | |
|---|---|
| **Default** | 0.01 (1 Micro-Lot) |
| **Beschreibung** | Positionsgroesse pro Grid-Level. |

### MaxActiveGrids
| | |
|---|---|
| **Default** | 3 |
| **Beschreibung** | Maximale Anzahl gleichzeitig aktiver Grids (verschiedene Symbole). |

### MaxLevelsPerCycle
| | |
|---|---|
| **Default** | 2 |
| **Beschreibung** | Maximale Anzahl Grid-Levels die pro Pruefzyklus getriggert werden. Schutz vor Price-Gaps wo der Preis mehrere Levels auf einmal durchschlaegt. |

### Counter-Fill-Logik

Wenn ein Buy-Level gefuellt wird und der Preis anschliessend um 1 Grid-Spacing steigt, wird die Position mit Gewinn geschlossen (Counter-Fill).

**Formel (Buy-Level):**
```
PnL = (AktuellerPreis - LevelPreis) × LotSizePerLevel
Bedingung: AktuellerPreis >= LevelPreis + SpacingPrice
```

Nach dem Counter-Fill wird das Level auf "Pending" zurueckgesetzt (Refill) und kann erneut getriggert werden.

---

## 5. Default SL/TP-Schutz

Wenn das LLM keinen Stop-Loss oder Take-Profit liefert, setzt der Bot automatische Defaults:

| | Wert | Formel |
|---|---|---|
| **Default Stop-Loss** | 50 Pips | `SL = Entry ∓ 50 × PipSize` |
| **Default Take-Profit** | 1.5× SL-Distanz | `TP = Entry ± |Entry - SL| × 1.5` |
| **Risk/Reward** | 1 : 1.5 | |

---

## 6. Pip-Berechnungen

### Pip-Groesse nach Instrument

| Typ | Instrumente | 1 Pip |
|---|---|---|
| Forex (Standard) | EURUSD, GBPUSD, AUDUSD, etc. | 0.0001 |
| JPY-Paare | USDJPY, EURJPY, GBPJPY | 0.01 |
| Gold | XAUUSD | 0.1 |
| Silber | XAGUSD | 0.01 |
| Indizes | US100, US500, DE30 | 1.0 |
| Oel | XTIUSD, XBRUSD | 0.01 |

### Pip-Wert pro Lot (in USD)

| Typ | Formel | Beispiel |
|---|---|---|
| XXX/USD (EURUSD) | `100.000 × PipSize` | $10 |
| USD/XXX (USDCHF) | `100.000 × PipSize / Preis` | ~$11.11 bei 0.90 |
| JPY-Paare | `100.000 × 0.01 / Preis` | ~$6.67 bei 150 |
| Gold | `100 × 0.1` | $10 |
| Indizes | `1 × 1.0` | $1 |

### Umrechnungen

```
Preisdifferenz → Pips:  PriceToPips(symbol, diff) = |diff| / PipSize
Pips → Preisdifferenz:  PipsToPrice(symbol, pips) = pips × PipSize
```

---

## 7. Empfohlene Einstellungen

### Konservativ (Anfaenger, kleines Konto)
```
MinConfidence:           0.75
RiskPerTradePercent:     1.0
MaxPositionSizePercent:  5.0
MaxOpenPositions:        3
MaxDailyLossPercent:     2.0
MaxDrawdownPercent:      10.0
StopLossPercent:         3.0
TrailingStopPips:        30
BreakevenTriggerPips:    20
TradingIntervalMinutes:  30
MaxSpreadPips:           3.0
```

### Moderat (Standard)
```
MinConfidence:           0.65
RiskPerTradePercent:     2.0
MaxPositionSizePercent:  10.0
MaxOpenPositions:        5
MaxDailyLossPercent:     3.0
MaxWeeklyLossPercent:    6.0
MaxDrawdownPercent:      15.0
StopLossPercent:         5.0
TrailingStopPips:        40
BreakevenTriggerPips:    25
PartialClosePercent:     0.5
PartialCloseTriggerPips: 30
TradingIntervalMinutes:  15
MaxSpreadPips:           3.0
```

### Aggressiv (erfahren, grosses Konto)
```
MinConfidence:           0.60
RiskPerTradePercent:     3.0
MaxPositionSizePercent:  15.0
MaxOpenPositions:        10
MaxDailyLossPercent:     5.0
MaxWeeklyLossPercent:    10.0
MaxDrawdownPercent:      20.0
StopLossPercent:         5.0
TrailingStopPips:        25
BreakevenTriggerPips:    15
PartialClosePercent:     0.3
PartialCloseTriggerPips: 20
TradingIntervalMinutes:  15
MaxSpreadPips:           2.0
MaxCorrelatedExposurePercent: 25.0
```
