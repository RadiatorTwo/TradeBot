## Konfigurationshandbuch – Trading‑Bot

Diese Dokumentation erklärt alle wichtigen Konfigurationswerte deines Trading‑Bots so, dass auch jemand ohne Trading‑Vorkenntnisse versteht:

- **Was steuert dieser Wert?**
- **Was passiert bei höheren/niedrigeren Werten?**
- **Wofür ist das gut / wann ändert man das?**

---

## 1. KI / LLM‑Einstellungen

### `LlmSettings.Provider`

- **Beschreibung**: Wählt, welcher KI‑Dienst benutzt wird (`"Gemini"`, `"Anthropic"`, `"OpenAICompatible"`).
- **Wirkung**: Bestimmt, welche KI die Marktanalysen und Trade‑Empfehlungen erstellt.
- **Typische Nutzung**:
  - `"Gemini"`: schnell, günstig, gut für Tests.
  - `"Anthropic"`: Fokus auf Qualität der Analysen.
  - `"OpenAICompatible"`: für eigene Server (z.B. Ollama, LM Studio, OpenRouter).

### `AnthropicSettings`, `GeminiSettings`, `OpenAICompatibleSettings`

- **`ApiKey` / `BaseUrl` / `Model` / `MaxTokens`**:
  - **ApiKey**: Zugangsschlüssel zum jeweiligen Dienst (wie ein Passwort speziell für APIs).
  - **BaseUrl**: Adresse des KI‑Servers (nur bei OpenAI‑kompatiblen Endpoints wichtig).
  - **Model**: Name des konkreten Modells, z.B. `"gemini-2.0-flash-lite"`.
  - **MaxTokens**: Wie viel Text die KI maximal auf einmal verarbeiten/ausgeben darf.
- **Wirkung**: Beeinflusst Kosten, Geschwindigkeit und Qualität der KI‑Antworten.

---

## 2. Account‑ und Broker‑Einstellungen

### `AccountConfig`

- **`Id` / `DisplayName`**:
  - Interne Kennung und Anzeigename für einen Trading‑Account (z.B. „Demo‑Account EURUSD“).
- **`TradeLocker` (`TradeLockerSettings`)**:
  - Verbindungsdaten zum Broker‑Konto (siehe unten).
- **`RiskManagement` (`RiskSettings`)**:
  - Alle Regeln, wie vorsichtig oder aggressiv gehandelt werden darf.
- **`PaperTrading` (`PaperTradingSettings`)**:
  - Ob der Bot nur simuliert („Papier‑Trading“) oder echte Orders an den Broker schickt.
- **`WatchList`**:
  - Liste von Symbolen, die beobachtet und gehandelt werden dürfen (z.B. `"EURUSD"`, `"DAX40"`).
- **`StrategyPrompt` / `StrategyLabel`**:
  - Text, mit dem man der KI grob erklärt, welche Art Strategie gewünscht ist (z.B. „Trendfolge auf H1“).
  - `StrategyLabel` ist eine kurze Bezeichnung (z.B. „Trend H1“).

### `TradeLockerSettings`

- **`BaseUrl`**:
  - Adresse der Broker‑API (Standard: Demo‑Umgebung).
- **`Email`, `Password`, `Server`, `AccountId`**:
  - Zugangsdaten zum Brokerkonto.
  - Ohne diese Werte kann der Bot zwar analysieren, aber keine echten Orders ausführen.

### `PaperTradingSettings`

- **`Enabled`**:
  - **true**: Der Bot simuliert Trades nur intern (kein echtes Geld).
  - **false**: Trades gehen direkt zum Broker (wenn dieser konfiguriert ist).
- **`InitialBalance`**:
  - Startkapital im Simulationskonto (z.B. 10.000 EUR).

---

## 3. Zentrales Risikomanagement (`RiskSettings`)

Diese Werte entscheiden im Kern, **ob** und **wie groß** der Bot überhaupt handelt.

### 3.1 Grund‑„Vertrauen“ in die KI

#### MinConfidence

- **Beschreibung**: Mindest‑„Vertrauenswert“ (0–1), den die KI ihrer Empfehlung gibt, damit der Bot sie überhaupt umsetzt.  
- **Höherer Wert**: Bot ist vorsichtiger, handelt nur bei sehr klaren Signalen → weniger Trades, meist bessere Qualität.
- **Niedrigerer Wert**: Bot ist risikofreudiger, handelt häufiger, auch bei unsicheren Situationen.

#### `DynamicConfidenceEnabled`

- **Beschreibung**: Wenn aktiv, passt der Bot den Mindest‑Vertrauenswert automatisch an:
  - bei hoher Markt‑Unruhe (Volatilität) → wird vorsichtiger,
  - bei schlechtem Lauf (mehrere Verluste) → wird vorsichtiger,
  - bei gutem Lauf → kann etwas mutiger werden.
- **Nutzen**: Der Bot reagiert auf die aktuelle Lage, anstatt starr immer denselben Schwellenwert zu nutzen.

#### `ConfidenceAtrFactor`, `ConfidenceDrawdownFactor`, `ConfidenceLossStreakFactor`

- **Beschreibung**: Fein‑Regler, wie stark sich der Vertrauens‑Schwellenwert erhöht bei:
  - `ConfidenceAtrFactor`: hoher Volatilität (starke Kursschwankungen).
  - `ConfidenceDrawdownFactor`: großem Rückgang des Kontos ab dem bisherigen Höchststand.
  - `ConfidenceLossStreakFactor`: schwacher Gewinn‑Quote (viele Verluste in Folge).
- **Höherer Wert**: Der Bot schraubt die Anforderungen an neue Trades stärker nach oben (wird konservativer).

#### `MaxDynamicConfidence`

- **Beschreibung**: Obergrenze, wie hoch der dynamische Vertrauens‑Schwellenwert maximal steigen darf (z.B. 0.85 = 85 %).

---

### 3.2 Positionsgröße und Exposures

#### `MaxPositionSizePercent`

- **Beschreibung**: Maximale Größe einer einzelnen Position im Verhältnis zum gesamten Konto (z.B. 10 %).
- **Beispiel**: Konto = 10.000 EUR, `MaxPositionSizePercent = 10` → ein Trade darf max. 1.000 EUR Wert bewegen.
- **Höherer Wert**: Einzelne Trades können sehr viel vom Konto bewegen → höheres Risiko.
- **Niedrigerer Wert**: Einzelne Trades sind kleiner → geringeres Risiko, aber evtl. auch niedrigere Gewinne pro Trade.

#### `MaxOpenPositions`

- **Beschreibung**: Maximale Anzahl gleichzeitig geöffneter Positionen (inkl. ausstehender Orders).
- **Wirkung**: Verhindert, dass der Bot sich auf zu viele Werte zugleich verteilt.

#### `RiskPerTradePercent`

- **Beschreibung**: Prozent des Kontos, das pro Trade maximal riskiert werden darf (bezogen auf den Stop‑Loss).  
  Beispiel: 1 % von 10.000 EUR → 100 EUR Risiko; daraus wird die Lotgröße berechnet.
- **0**: Automatische Berechnung ist aus; der Bot nutzt vordefinierte oder vom LLM vorgeschlagene Lots.
- **Höherer Wert**: Mehr Risiko pro Trade: Kontobewegungen werden größer.

---

### 3.3 Tages‑, Wochen‑, Monats‑Limits & Drawdown

#### `MaxDailyLossPercent` und `MaxDailyLossAbsolute`

- **Beschreibung**: Maximale Verluste **pro Tag**, sowohl:
  - in Prozent vom Startkapital dieses Tages (`MaxDailyLossPercent`),
  - als absoluter Betrag (`MaxDailyLossAbsolute`, z.B. 500 EUR).
- **Wirkung**: Wenn eines dieser Limits überschritten wird, aktiviert der Bot einen sogenannten **Kill Switch** und stoppt neue Trades.

#### `MaxDrawdownPercent`

- **Beschreibung**: Maximaler erlaubter Rückgang vom bisher höchsten Kontostand (z.B. 20 %).  
  „Drawdown“ = wie viel das Konto vom Höchststand runtergefallen ist.
- **Wirkung**: Wird dieses Limit überschritten, beendet der Bot das Trading (Kill Switch).

#### `MaxWeeklyLossPercent`, `MaxMonthlyLossPercent`

- **Beschreibung**: Maximale Verluste pro Woche/Monat im Vergleich zu einem Referenz‑Kontostand am Anfang der Woche/des Monats.
- **Wirkung**: Wenn erreicht, blockiert der Bot **neue** Trades in dieser Woche / diesem Monat.

---

### 3.4 Spread, Korrelation und Portfolio‑Verteilung

#### `MaxSpreadPips`

- **Beschreibung**: Maximale erlaubte Spanne zwischen Kauf‑ und Verkaufskurs (Spread) in Pips.  
  Ein „Pip“ ist eine sehr kleine Preiseinheit (z.B. 0,0001 bei EURUSD).
- **Höherer Wert**: Der Bot akzeptiert Trades auch bei teuren/illiquiden Marktphasen.
- **Niedrigerer Wert**: Trades werden bei hohen Spreads abgelehnt, was Kosten reduziert.

#### `MaxCorrelatedExposurePercent`

- **Beschreibung**: Maximaler Gesamt‑Anteil, den der Bot in **stark miteinander zusammenhängende Werte** investieren darf.  
  Beispiel: EURUSD und GBPUSD bewegen sich oft ähnlich → stark korreliert.
- **Wirkung**: Verhindert, dass der Bot effektiv „alles auf eine Karte“ setzt, nur über verschiedene, aber ähnliche Werte.

#### `CorrelationThreshold`

- **Beschreibung**: Ab welcher Stärke zwei Symbole als „korreliert“ gelten (z.B. ab 0.3 = 30 % Zusammenhang).

#### `PortfolioAllocationSettings` (`Allocation`)

- **`Enabled`**:
  - Ob Portfolio‑Verteilung aktiv ist (z.B. „kein Symbol soll mehr als 20 % des Kontos ausmachen“).
- **`DefaultMaxPercent`**:
  - Standard‑Obergrenze pro Symbol (z.B. 20 % des Kontos).
- **`SymbolLimits`**:
  - Individuelle Obergrenzen für bestimmte Symbole (z.B. `{"XAUUSD": 10.0}`).
- **`RebalanceTriggerOverPercent`**:
  - Wie weit ein Symbol über seine Grenze hinausschießen darf, bevor automatisch „abgebaut“ wird (z.B. +2 %).

---

### 3.5 Stop‑Loss, Trailing, Breakeven, Teilverkäufe

#### `StopLossPercent`

- **Beschreibung**: Lokaler Schutz: Schließt eine Position, wenn sie mehr als diesen Prozentsatz im Verlust ist (z.B. 5 %).
- **Nutzen**: Fängt Fälle ab, in denen kein oder ein falscher Stop‑Loss gesetzt wurde.

#### `DefaultStopLossPips` und `DefaultTakeProfitRatio`

- **`DefaultStopLossPips`**:
  - Fallback‑Abstand des Stop‑Loss in Pips, wenn die KI keinen Stop‑Loss liefert.
- **`DefaultTakeProfitRatio`**:
  - Wie weit das Gewinnziel im Verhältnis zum Stop‑Loss liegt (z.B. 1,5‑mal so weit wie der SL).

#### `MinRiskRewardRatio`

- **Beschreibung**: Mindest‑Verhältnis von möglichem Gewinn zu möglichem Verlust (z.B. 1.0 = mindestens 1:1).
- **Wirkung**: Ist das Chance/Risiko‑Verhältnis schlechter als dieser Wert, wird der Trade abgelehnt.

#### `RequireSlTpFromLlm`

- **Beschreibung**: 
  - **true**: Die KI muss explizit Stop‑Loss und Take‑Profit liefern, sonst wird der Trade abgelehnt.
  - **false**: Der Bot nutzt seine Default‑Regeln, falls die KI nichts liefert.

#### `TrailingStopPips`

- **Beschreibung**: Abstand in Pips, ab dem ein **nachziehender Stop‑Loss** aktiviert wird.  
  Wenn der Trade im Gewinn ist, zieht der Stop‑Loss „hinterher“, um Gewinne zu sichern.
- **0**: Funktion ist aus.

#### `BreakevenTriggerPips`

- **Beschreibung**: Ab wie vielen Pips Gewinn der Bot den Stop‑Loss auf ungefähr „Einstiegspreis + Minimalpuffer“ setzt.  
  → Das Risiko wird auf Null reduziert, wenn der Trade gut läuft.

#### `PartialClosePercent` und `PartialCloseTriggerPips`

- **Beschreibung**:
  - `PartialCloseTriggerPips`: ab wie vielen Pips Gewinn geprüft wird, ob ein Teil der Position geschlossen wird.
  - `PartialClosePercent`: welcher Anteil (z.B. 50 % = 0.5) geschlossen wird.
- **Nutzen**: Ein Teil wird mit Gewinn gesichert, der Rest kann weiterlaufen.

---

### 3.6 Pyramiding (Positionsaufbau in gleicher Richtung)

#### `MaxPyramidLevels`

- **Beschreibung**: Wie oft der Bot zusätzlich in die gleiche Richtung aufstocken darf, wenn schon eine Position offen ist.
- **0**: Kein Pyramiding – zusätzliche Signale in gleiche Richtung werden ignoriert.

#### `PyramidMinConfidence`

- **Beschreibung**: Mindest‑Vertrauen der KI, damit eine zusätzliche Pyramiding‑Position erlaubt wird.

---

### 3.7 Verhalten bei Gegenrichtung und Sessions

#### `OppositeDirectionMinConfidence`

- **Beschreibung**: Mindest‑Vertrauen der KI, damit bestehende Positionen **in der Gegenrichtung geschlossen** werden.  
  Wenn 0, wird automatisch `MinConfidence` genutzt.
- **Nutzen**: Verhindert, dass die KI leichtfertig laufende Positionen umdreht.

#### `AllowedSessions`

- **Beschreibung**: Liste erlaubter Handelszeiten oder ‑Sessions (z.B. nur London/NY‑Session).  
  Wenn leer, greifen Default‑Regeln des `TradingSessionService`.

---

### 3.8 Taktung & Datenmenge

#### `TradingIntervalMinutes`

- **Beschreibung**: Wie oft der Bot einen vollständigen „Analyse und Trades ausführen“‑Zyklus startet (z.B. alle 15 Minuten).
- **Kürzere Intervalle**: Mehr Trades, mehr Kosten/Last, schnellere Reaktion, aber auch mehr Rauschen.
- **Längere Intervalle**: Weniger Trades, eher mittel‑/langfristig.

#### `AnalysisDelaySeconds`

- **Beschreibung**: Pause zwischen der Analyse einzelner Symbole innerhalb eines Zyklus (z.B. 2 Sekunden).  
  Schützt vor API‑Limits und zu schneller Abfolge.

#### `IndicatorCandleCount`

- **Beschreibung**: Wie viele historische Kursdaten (Kerzen) für technische Indikatoren geladen werden (z.B. 210 Stück).  
  Muss mindestens so groß sein wie der längste Indikator (EMA200).

#### `RecentPricesCount`

- **Beschreibung**: Wie viele der letzten Preise in vereinfachte Berechnungen (z.B. Tagesänderung) einfließen.

#### `FeedbackLoopTradeCount`

- **Beschreibung**: Wie viele vergangene Trades das LLM als Feedback bekommt, um aus früheren Erfolgen/Misserfolgen zu lernen (z.B. 10).

#### `PendingOrderMaxAgeMinutes`

- **Beschreibung**: Maximale Lebensdauer von noch nicht ausgeführten Limit/Stop‑Orders, bevor sie aufgeräumt/überprüft werden.

#### `LlmRetryCount`

- **Beschreibung**: Wie oft der Bot erneut bei der KI nachfragt, wenn die erste Antwort fehlschlägt (zusätzlich zum ersten Versuch).  
  Beispiel: `1` bedeutet insgesamt 2 Versuche.

---

## 4. Grid‑Trading‑Spezifische Einstellungen (`GridSettings`)

Grid‑Trading ist eine spezielle Strategie: Es werden mehrere Kauf‑/Verkaufs‑Levels um einen Mittelpreis gelegt.

### `Enabled`

- **Beschreibung**: Ob Grid‑Trading überhaupt genutzt werden darf.
- **false**: Bot handelt normal ohne Grids.

### `GridSpacingPips`

- **Beschreibung**: Abstand in Pips zwischen den Grid‑Levels (z.B. 20 Pips).  
  → Wie weit liegen die Kauf‑ und Verkaufszonen voneinander entfernt?

### `GridLevelsAbove` / `GridLevelsBelow`

- **Beschreibung**:
  - `GridLevelsAbove`: Anzahl Verkaufs‑Levels **oberhalb** des Mittelpreises.
  - `GridLevelsBelow`: Anzahl Kauf‑Levels **unterhalb** des Mittelpreises.
- **Beispiel**: 5 Levels über und 5 unter dem Center ergeben 10 Levels insgesamt.

### `LotSizePerLevel`

- **Beschreibung**: Wie groß jede einzelne Grid‑Position ist (z.B. 0,01 Lot).

### `MaxActiveGrids`

- **Beschreibung**: Wie viele verschiedene Symbole gleichzeitig ein aktives Grid haben dürfen (z.B. 3).

### `MaxLevelsPerCycle`

- **Beschreibung**: Wie viele Levels innerhalb eines Trading‑Zyklus maximal ausgelöst werden dürfen.  
  Schützt vor extrem vielen Fills bei plötzlichen großen Kurssprüngen (Gaps).

### `MinGridDurationMinutes`

- **Beschreibung**: Mindestlaufzeit, bevor ein Grid wieder deaktiviert werden kann.  
  Verhindert, dass Grids zu kurz laufen und andauernd neu erstellt/abgebrochen werden.

### `GridDeactivationMinConfidence`

- **Beschreibung**: Mindest‑Vertrauen der KI, damit ein bestehendes Grid deaktiviert und in eine klare Richtung (BUY/SELL) gewechselt wird.

---

## 5. Multi‑Timeframe‑Bestätigung (`MultiTimeframeSettings`)

### `Enabled`

- **Beschreibung**: Ob ein zusätzlicher Trendfilter aktiv ist, der auf einem **höheren Zeitrahmen** (z.B. 4‑Stunden‑Chart) misst, ob der Markt eher steigt oder fällt.

### `HigherTimeframe`

- **Beschreibung**: Welcher höhere Zeitrahmen zur Trendbestimmung genutzt wird (z.B. `"4H"` = 4 Stunden).

### `EmaPeriod`

- **Beschreibung**: Länge des gleitenden Durchschnitts (EMA) zur Trendbestimmung, z.B. 200 Kerzen.  
- **Wirkung**:
  - EMA über aktuellem Kurs → Abwärtstrend.
  - EMA unter aktuellem Kurs → Aufwärtstrend.
  Der Filter kann Trades verbieten, die **gegen diesen Trend** laufen.

---

## 6. News‑ und Sentiment‑Einstellungen (`NewsSettings`)

### `Enabled`

- **Beschreibung**: Ob Nachrichten‑/Sentiment‑Daten in die Entscheidungen einfließen sollen.

### `FinnhubApiKey`

- **Beschreibung**: Zugangsschlüssel zu einer News‑API (z.B. Finnhub), um Schlagzeilen zu laden.

### `MaxHeadlinesPerSymbol`

- **Beschreibung**: Wie viele Nachrichten‑Überschriften pro Symbol maximal berücksichtigt werden (z.B. 5).

### `RefreshIntervalMinutes`

- **Beschreibung**: Wie oft Nachrichten je Symbol aktualisiert werden (z.B. alle 60 Minuten).

---

## 7. Zusammenfassung in Alltagssprache

- **Die LLM‑Einstellungen** bestimmen, welche KI die Marktanalysen macht und wie viel Kontext sie bekommt.
- **Die Risk‑Settings** entscheiden, wie risikofreudig der Bot ist:
  - wie groß einzelne Trades sein dürfen,
  - wie viele Verluste pro Tag/Woche/Monat erlaubt sind,
  - wann der Bot komplett stoppen muss.
- **Grid‑ und Multi‑Timeframe‑Settings** sind fortgeschrittene Filter und Strategien, die Trades nur zulassen, wenn der Markt in einem passenden Trend‑ oder Preisbereich ist.
- **News/Correlation/Portfolio‑Settings** sorgen dafür, dass der Bot nicht alles auf eine Karte setzt – weder auf ein einzelnes Symbol noch auf mehrere, die sich gleich bewegen.