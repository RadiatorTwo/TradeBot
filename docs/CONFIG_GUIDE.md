## Konfigurationshandbuch – Trading‑Bot

Diese Dokumentation erklärt alle wichtigen Konfigurationswerte deines Trading‑Bots so, dass auch jemand ohne Trading‑Vorkenntnisse versteht:

- **Was steuert dieser Wert?**
- **Was passiert bei höheren/niedrigeren Werten?**
- **Wofür ist das gut / wann ändert man das?**

---

## 1. KI / LLM‑Einstellungen (Globale Einstellungen)

*Zu finden unter: Einstellungen > API-Konfiguration (nur Anzeige)*
*Änderung: Nur über `appsettings.json` möglich.*

### LLM Provider (`LlmSettings.Provider`)

- **Beschreibung**: Wählt, welcher KI‑Dienst benutzt wird (`"Gemini"`, `"Anthropic"`, `"OpenAICompatible"`).
- **Wirkung**: Bestimmt, welche KI die Marktanalysen und Trade‑Empfehlungen erstellt.
- **Typische Nutzung**:
  - `"Gemini"`: schnell, günstig, gut für Tests.
  - `"Anthropic"`: Fokus auf Qualität der Analysen.
  - `"OpenAICompatible"`: für eigene Server (z.B. Ollama, LM Studio, OpenRouter).

### API Keys & Models (`AnthropicSettings`, `GeminiSettings`, etc.)

- **`ApiKey`**: Zugangsschlüssel zum jeweiligen Dienst (wie ein Passwort).
- **`BaseUrl`**: Adresse des KI‑Servers (nur bei OpenAI‑kompatiblen Endpoints).
- **`Model`**: Name des konkreten Modells (z.B. `claude-sonnet-4...` oder `gemini-2.0...`).
- **`MaxTokens`**: Maximale Antwortlänge der KI.

---

## 2. Account‑ und Broker‑Einstellungen

*Zu finden unter: Accounts > [Account bearbeiten]*

### Kurzname / ID (`Id`)

- **UI-Label**: Kurzname (ID)
- **Beschreibung**: Interne Kennung für einen Trading‑Account (z.B. „Demo1“).

### Anzeigename (`DisplayName`)

- **UI-Label**: Anzeigename
- **Beschreibung**: Freundlicher Name für die Anzeige im Dashboard.

### Broker-Verbindung (`TradeLockerSettings`)

- **Server-URL (`BaseUrl`)**: Adresse der Broker‑API (Demo oder Live).
- **Server-Name (`Server`)**: Name des Servers beim Broker (z.B. `OSP-DEMO`).
- **Email / Passwort**: Zugangsdaten zum Brokerkonto.
- **TradeLocker Account-ID**: Spezifische Konto-ID, falls der Login mehrere Konten hat.

### Paper-Trading (`PaperTradingSettings`)

- **Paper-Trading (`Enabled`)**:
  - **true**: Der Bot simuliert Trades nur intern (kein echtes Geld).
  - **false**: Trades gehen direkt zum Broker.
- **Paper-Startkapital (`InitialBalance`)**: Startkapital für die Simulation.

### Watchlist (`WatchList`)

- **UI-Label**: Symbole (komma-separiert)
- **Beschreibung**: Liste von Symbolen, die dieser Account handeln darf (z.B. `EURUSD, XAUUSD`).
- **Hinweis**: Wenn leer, wird die globale Watchlist aus den Einstellungen verwendet.

### Strategie (`StrategyPrompt`, `StrategyLabel`)

- **Strategie-Label**: Kurze Bezeichnung (z.B. „Trend H1“).
- **Custom System-Prompt**: Text, mit dem man der KI die Strategie erklärt. Wird an den Standard-Prompt angehängt.

---

## 3. Zentrales Risikomanagement (`RiskSettings`)

*Zu finden unter: Accounts > [Account bearbeiten] > Risiko-Parameter*

### 3.1 Grund‑„Vertrauen“ in die KI

#### Min. Confidence (`MinConfidence`)

- **Beschreibung**: Mindest‑„Vertrauenswert“ (0–1), den die KI ihrer Empfehlung gibt, damit der Bot sie überhaupt umsetzt.
- **Höherer Wert**: Bot ist vorsichtiger, handelt nur bei sehr klaren Signalen.
- **Niedrigerer Wert**: Bot handelt häufiger, auch bei unsicheren Situationen.

#### Dynamische Confidence (`DynamicConfidenceEnabled`)

- **Beschreibung**: Wenn aktiv, passt der Bot den Mindest‑Vertrauenswert automatisch an (bei Volatilität oder Verlustserien).

#### Confidence-Faktoren (Erweitert)

- **Confidence ATR Faktor**: Erhöht Min. Confidence bei hoher Volatilität.
- **Confidence Drawdown Faktor**: Erhöht Min. Confidence bei Drawdown.
- **Confidence Loss Streak Faktor**: Erhöht Min. Confidence bei Verlustserie.
- **Max. Dynamic Confidence**: Obergrenze für den dynamischen Wert.
- **Confidence Win-Rate-Schwelle**: Ab welcher Win-Rate die Confidence angepasst wird.

---

### 3.2 Positionsgröße und Exposures

#### Risiko pro Trade % (`RiskPerTradePercent`)

- **Beschreibung**: Prozent des Kontos, das pro Trade maximal riskiert werden darf (bezogen auf den Stop‑Loss).
- **0**: Automatische Berechnung ist aus; der Bot nutzt vordefinierte Lots.

#### Max. Positionsgröße % (`MaxPositionSizePercent`)

- **Beschreibung**: Maximale Größe einer einzelnen Position im Verhältnis zum gesamten Konto.

#### Max. offene Positionen (`MaxOpenPositions`)

- **Beschreibung**: Maximale Anzahl gleichzeitig geöffneter Positionen.

---

### 3.3 Tages‑, Wochen‑, Monats‑Limits & Drawdown

*Zu finden unter: Accounts > [Account bearbeiten] > Verlustlimits*

#### Max. Tagesverlust (`MaxDailyLossPercent` / `MaxDailyLossAbsolute`)

- **Beschreibung**: Maximale Verluste **pro Tag** (in % oder absolutem Betrag).
- **Wirkung**: Kill Switch (Trading-Stopp) für den Rest des Tages.

#### Max. Drawdown vom Peak (`MaxDrawdownPercent`)

- **Beschreibung**: Maximaler erlaubter Rückgang vom bisher höchsten Kontostand.
- **Wirkung**: Kill Switch bei Überschreitung.

#### Max. Wochen-/Monatsverlust (`MaxWeeklyLossPercent`, `MaxMonthlyLossPercent`)

- **Beschreibung**: Limits für Wochen- und Monatsverluste.

---

### 3.4 Spread, Korrelation und Portfolio

#### Max. Spread (`MaxSpreadPips`)

- **UI-Label**: Max. Spread (Pips)
- **Beschreibung**: Maximale erlaubte Spanne zwischen Kauf‑ und Verkaufskurs.

#### Max. korrelierte Exposure (`MaxCorrelatedExposurePercent`)

- **UI-Label**: Max. korrelierte Exposure (%)
- **Beschreibung**: Maximaler Gesamt‑Anteil in stark zusammenhängenden Werten (z.B. EURUSD + GBPUSD).

#### Korrelations-Schwelle (`CorrelationThreshold`)

- **Beschreibung**: Ab welcher Stärke zwei Symbole als „korreliert“ gelten.

#### Portfolio & Allokation (`PortfolioAllocationSettings`)

*Zu finden unter: Accounts > [Account bearbeiten] > Portfolio & Allokation*

- **Aktivieren (`Enabled`)**: Schaltet die Portfolio-Regeln ein.
- **Default Max. % pro Symbol (`DefaultMaxPercent`)**: Kein Symbol darf mehr als X % des Kontos ausmachen.
- **Rebalance Trigger (`RebalanceTriggerOverPercent`)**: Toleranzbereich, bevor Positionen reduziert werden.

---

### 3.5 Stop‑Loss, Trailing, Breakeven

*Zu finden unter: Accounts > [Account bearbeiten] > Gewinnschutz*

#### Stop-Loss % (`StopLossPercent`)

- **Beschreibung**: Harter Notfall-Stop-Loss in Prozent vom Einstiegspreis.

#### Default SL/TP (`DefaultStopLossPips`, `DefaultTakeProfitRatio`)

- **Default SL**: Fallback, wenn die KI keinen Stop-Loss liefert.
- **Default TP-Ratio**: Verhältnis von Gewinnziel zu Stop-Loss (z.B. 1.5 = 150% des Risikos als Gewinn).

#### Min. Risk/Reward (`MinRiskRewardRatio`)

- **Beschreibung**: Mindest‑Verhältnis von Chance zu Risiko. Trades mit schlechterem Verhältnis werden abgelehnt.

#### SL/TP vom LLM erforderlich (`RequireSlTpFromLlm`)

- **true**: KI muss zwingend SL/TP liefern.
- **false**: Bot nutzt Defaults, falls KI nichts liefert.

#### Trailing Stop (`TrailingStopPips`)

- **Beschreibung**: Zieht den Stop-Loss hinterher, wenn der Trade im Gewinn ist.

#### Breakeven (`BreakevenTriggerPips`)

- **Beschreibung**: Setzt Stop-Loss auf Einstiegspreis, sobald X Pips Gewinn erreicht sind.

#### Partial Close (`PartialClosePercent`, `PartialCloseTriggerPips`)

- **Beschreibung**: Schließt einen Teil der Position (Prozent) bei Erreichen von X Pips Gewinn.

---

### 3.6 Pyramiding (Erweitert)

*Zu finden unter: Accounts > [Account bearbeiten] > Risiko-Parameter*

#### Max. Pyramid Levels (`MaxPyramidLevels`)

- **Beschreibung**: Wie oft zu einer bestehenden Position hinzugekauft werden darf.
- **0**: Deaktiviert.

#### Pyramid Min. Confidence (`PyramidMinConfidence`)

- **Beschreibung**: Erforderliches KI-Vertrauen für Pyramiding-Trades.

---

### 3.7 Gegenrichtung und Sessions

#### Gegenrichtung Min. Confidence (`OppositeDirectionMinConfidence`)

- **Beschreibung**: Mindest-Vertrauen, um bestehende Trades in die Gegenrichtung zu schließen (Reverse).

#### Erlaubte Sessions (`AllowedSessions`)

- **Beschreibung**: Liste erlaubter Handelszeiten (z.B. "London, NewYork").

#### Pending Order Max. Age (`PendingOrderMaxAgeMinutes`)

- **Beschreibung**: Wie lange Limit-Orders gültig bleiben (in Minuten).

---

### 3.8 Taktung & Datenmenge

#### Analyse-Intervall (`TradingIntervalMinutes`)

- **Beschreibung**: Wie oft der Bot den Markt analysiert (z.B. alle 15 Min).

#### Analyse-Pause (`AnalysisDelaySeconds`)

- **Beschreibung**: Pause zwischen der Analyse einzelner Symbole (API-Schutz).

#### Indikator-Candles (`IndicatorCandleCount`)

- **Beschreibung**: Anzahl historischer Kerzen für Indikatoren.

#### Recent Prices (`RecentPricesCount`)

- **Beschreibung**: Anzahl Preise für Kurzzeit-Analyse.

#### Feedback-Loop Trades (`FeedbackLoopTradeCount`)

- **Beschreibung**: Anzahl vergangener Trades, die der KI zur Analyse gegeben werden.

#### LLM-Retries (`LlmRetryCount`)

- **Beschreibung**: Anzahl Wiederholungsversuche bei KI-Fehlern.

---

## 4. Grid‑Trading‑Einstellungen (`GridSettings`)

*Zu finden unter: Accounts > [Account bearbeiten] > Grid-Trading*

### Grid-Trading aktivieren (`Enabled`)

- **Beschreibung**: Schaltet den Grid-Modus ein (Kauf/Verkauf in Staffeln).

### Grid-Parameter

- **Grid-Spacing (`GridSpacingPips`)**: Abstand zwischen den Levels.
- **Lot pro Level (`LotSizePerLevel`)**: Größe jeder einzelnen Position.
- **Levels oberhalb/unterhalb**: Anzahl der Kauf-/Verkaufs-Staffeln.
- **Max. aktive Grids**: Wie viele Symbole gleichzeitig im Grid-Modus sein dürfen.
- **Max. Levels pro Zyklus**: Begrenzung für schnelle Marktbewegungen.
- **Min. Grid-Dauer (`MinGridDurationMinutes`)**: Mindestlaufzeit eines Grids.
- **Deaktivierung Min. Confidence**: Wann ein Grid aufgelöst wird, wenn die KI die Meinung ändert.

---

## 5. Multi‑Timeframe‑Bestätigung (`MultiTimeframeSettings`)

*Zu finden unter: Einstellungen (Global)*

### Multi-Timeframe-Filter aktivieren (`Enabled`)

- **Beschreibung**: Prüft Trend auf höherem Timeframe (z.B. 4H) vor jedem Trade.

### Parameter

- **Höherer Timeframe**: z.B. "4H" oder "1D".
- **EMA-Periode**: Länge des Trend-Indikators (z.B. 200).

---

## 6. News‑ und Sentiment‑Einstellungen (`NewsSettings`)

*Zu finden unter: Einstellungen (Global) > News & Sentiment*

### News-Sentiment-Analyse aktivieren (`Enabled`)

- **Beschreibung**: Ob Nachrichten in die KI-Entscheidung einfließen.

### Parameter

- **Finnhub API Key**: Schlüssel für den News-Provider.
- **Max. Headlines**: Anzahl Schlagzeilen pro Symbol.
- **Refresh (Min.)**: Wie oft News aktualisiert werden.
