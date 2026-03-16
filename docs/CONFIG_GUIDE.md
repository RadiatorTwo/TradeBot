## Konfigurationshandbuch – Trading‑Bot

Diese Dokumentation erklärt alle wichtigen Konfigurationswerte deines Trading‑Bots so, dass auch jemand ohne Trading‑Vorkenntnisse versteht:

- **Was steuert dieser Wert?**
- **Was passiert bei höheren/niedrigeren Werten?**
- **Wofür ist das gut / wann ändert man das?**

Die Account‑Einstellungen sind in der Oberfläche in **Tabs** gruppiert. Dieses Handbuch folgt derselben Struktur.

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

## 2. Account‑Einstellungen (pro Account)

*Zu finden unter: Accounts > [Neuer Account] bzw. [Bearbeiten]*

Die Einstellungen eines Accounts sind in der UI in folgende Tabs gegliedert. Die Reihenfolge und Gruppierung unten entspricht der Oberfläche.

---

### 2.1 Tab: Allgemein

*Zu finden unter: Accounts > [Account bearbeiten] > Tab „Allgemein“*

#### Kurzname / ID (`Id`)

- **UI-Label**: Kurzname (ID)
- **Beschreibung**: Interne Kennung für diesen Trading‑Account (z.B. „Demo1“).
- **Hinweis**: Änderung erfordert Neustart.

#### Anzeigename (`DisplayName`)

- **UI-Label**: Anzeigename
- **Beschreibung**: Freundlicher Name für die Anzeige im Dashboard und in Listen.

---

### 2.2 Tab: Broker

*Zu finden unter: Accounts > [Account bearbeiten] > Tab „Broker“*

#### Broker-Verbindung (`TradeLockerSettings`)

- **Server-URL (`BaseUrl`)**: Adresse der Broker‑API (Demo oder Live).
- **Server-Name (`Server`)**: Name des Servers beim Broker (z.B. `OSP-DEMO`).
- **Email / Passwort**: Zugangsdaten zum Brokerkonto.
- **TradeLocker Account-ID**: Spezifische Konto-ID, falls der Login mehrere Konten hat. Leer = erster Account.

---

### 2.3 Tab: Paper & Risiko

*Zu finden unter: Accounts > [Account bearbeiten] > Tab „Paper & Risiko“*

Enthält Paper-Trading sowie alle Parameter für Vertrauen in die KI, Positionsgröße, Taktung und erweiterte Risiko-Optionen.

#### Paper-Trading (`PaperTradingSettings`)

- **Paper-Trading (`Enabled`)**:
  - **true**: Der Bot simuliert Trades nur intern (kein echtes Geld).
  - **false**: Trades gehen direkt zum Broker.
- **Paper-Startkapital (`InitialBalance`)**: Startkapital für die Simulation in $.

#### Grund‑„Vertrauen“ in die KI

- **Min. Confidence (`MinConfidence`)**: Mindest‑Vertrauenswert (0–1), den die KI ihrer Empfehlung geben muss. Höher = vorsichtiger, niedriger = häufigeres Handeln.
- **Dynamische Confidence (`DynamicConfidenceEnabled`)**: Passt den Mindestwert automatisch an (Volatilität, Verlustserien).
- **Confidence-Faktoren (Erweitert)**: Max. Dynamic Confidence, Confidence ATR / Drawdown / Loss-Streak-Faktor, Confidence Win-Rate-Schwelle.

#### Positionsgröße und Limits

- **Risiko pro Trade % (`RiskPerTradePercent`)**: Max. Risiko pro Trade in % des Kontos (bezogen auf SL). 0 = LLM entscheidet Lotgröße.
- **Max. Positionsgröße % (`MaxPositionSizePercent`)**: Maximale Größe einer Einzelposition relativ zum Konto.
- **Max. offene Positionen (`MaxOpenPositions`)**: Maximale Anzahl gleichzeitig offener Positionen.

#### Taktung & Analyse

- **Analyse-Intervall (`TradingIntervalMinutes`)**: Wie oft der Bot den Markt analysiert (z.B. alle 15 Min).
- **Analyse-Pause (`AnalysisDelaySeconds`)**: Pause zwischen Symbol-Analysen (API-Schutz).
- **Indikator-Candles (`IndicatorCandleCount`)**: Anzahl historischer Kerzen für Indikatoren (z.B. EMA200).
- **Recent Prices (`RecentPricesCount`)**: Anzahl Preise für die Kurzzeit-Analyse.
- **Feedback-Loop Trades (`FeedbackLoopTradeCount`)**: Anzahl vergangener Trades, die der KI im Prompt mitgegeben werden.
- **LLM-Retries (`LlmRetryCount`)**: Wiederholungsversuche bei KI-Fehlern (0 = kein Retry).

#### SL/TP-Defaults und Anforderungen

- **Default SL (Pips) (`DefaultStopLossPips`)**: Fallback-Stop-Loss, wenn die KI keinen liefert.
- **Default TP-Ratio (`DefaultTakeProfitRatio`)**: Verhältnis Gewinnziel zu SL (z.B. 1,5 = 150 % des Risikos).
- **Min. Risk/Reward (`MinRiskRewardRatio`)**: Trades mit schlechterem Verhältnis werden abgelehnt.
- **SL/TP vom LLM erforderlich (`RequireSlTpFromLlm`)**: true = KI muss SL/TP liefern, sonst Ablehnung.

#### Spread, Korrelation, Pyramid

- **Max. Spread (Pips) (`MaxSpreadPips`)**: Trade wird abgelehnt bei höherem Spread. 0 = deaktiviert.
- **Korrelations-Schwelle (`CorrelationThreshold`)**: Ab welcher Stärke zwei Symbole als korreliert gelten.
- **Max. Pyramid Levels (`MaxPyramidLevels`)**: Wie oft nachgekauft werden darf. 0 = deaktiviert.
- **Pyramid Min. Confidence (`PyramidMinConfidence`)**: Erforderliches KI-Vertrauen für Pyramiding.
- **Gegenrichtung Min. Confidence (`OppositeDirectionMinConfidence`)**: Mindest-Vertrauen, um Positionen in Gegenrichtung zu schließen. 0 = MinConfidence nutzen.

#### Sessions und Pending Orders

- **Erlaubte Sessions (`AllowedSessions`)**: Komma-getrennte Liste (z.B. „London, NewYork“). Leer = alle Sessions erlaubt.
- **Pending Order Max. Age (Min.) (`PendingOrderMaxAgeMinutes`)**: Maximale Gültigkeitsdauer von Limit-Orders in Minuten.

---

### 2.4 Tab: Verlust & Gewinn

*Zu finden unter: Accounts > [Account bearbeiten] > Tab „Verlust & Gewinn“*

#### Verlustlimits

- **Stop-Loss % (`StopLossPercent`)**: Lokaler Notfall-Stop-Loss in % vom Einstiegspreis.
- **Max. Tagesverlust (`MaxDailyLossPercent` / `MaxDailyLossAbsolute`)**: Kill Switch pro Tag (% oder $).
- **Max. Wochenverlust (%) (`MaxWeeklyLossPercent`)**: 0 = deaktiviert.
- **Max. Monatsverlust (%) (`MaxMonthlyLossPercent`)**: 0 = deaktiviert.
- **Max. Drawdown vom Peak (%) (`MaxDrawdownPercent`)**: Kill Switch bei Überschreitung. 0 = deaktiviert.
- **Max. korrelierte Exposure (%) (`MaxCorrelatedExposurePercent`)**: Blockiert Trades bei hoher Korrelation. 0 = deaktiviert.

#### Gewinnschutz

- **Trailing Stop (Pips) (`TrailingStopPips`)**: SL folgt dem Kurs im Gewinn. 0 = deaktiviert.
- **Breakeven ab (Pips) (`BreakevenTriggerPips`)**: SL wird auf Einstiegspreis gesetzt bei X Pips Gewinn. 0 = deaktiviert.
- **Partial Close (%) (`PartialClosePercent`)**: Anteil der Position, der bei TP1 geschlossen wird (0–1). 0 = deaktiviert.
- **Partial Close ab (Pips) (`PartialCloseTriggerPips`)**: Gewinn in Pips, ab dem Partial Close ausgelöst wird.

---

### 2.5 Tab: Portfolio & Grid

*Zu finden unter: Accounts > [Account bearbeiten] > Tab „Portfolio & Grid“*

#### Portfolio & Allokation (`PortfolioAllocationSettings`)

- **Portfolio-Allokation aktivieren (`Enabled`)**: Schaltet die Portfolio-Regeln ein.
- **Default Max. % pro Symbol (`DefaultMaxPercent`)**: Kein Symbol darf mehr als X % des Kontos ausmachen.
- **Rebalance Trigger (+%) (`RebalanceTriggerOverPercent`)**: Toleranz über Max. %, bevor Positionen reduziert werden.

#### Grid-Trading (`GridSettings`)

- **Grid-Trading aktivieren (`Enabled`)**: Automatische Grid-Orders bei Seitwärtsmärkten.
- **Grid-Spacing (Pips) (`GridSpacingPips`)**: Abstand zwischen den Levels.
- **Lot pro Level (`LotSizePerLevel`)**: Größe pro Grid-Position.
- **Levels oberhalb / unterhalb**: Anzahl Sell- bzw. Buy-Levels um das Center.
- **Max. aktive Grids**: Wie viele Symbole gleichzeitig im Grid-Modus sein dürfen.
- **Max. Levels pro Zyklus**: Begrenzung für schnelle Marktbewegungen (Gap-Schutz).
- **Grid-Deaktivierung Min. Confidence**: Min. Confidence, um ein Grid bei KI-Umkehr (buy/sell) zu deaktivieren.
- **Min. Grid-Dauer (Min.) (`MinGridDurationMinutes`)**: Mindestlaufzeit, bevor ein Grid deaktiviert werden kann.

---

### 2.6 Tab: Watchlist

*Zu finden unter: Accounts > [Account bearbeiten] > Tab „Watchlist“*

#### Symbole (`WatchList`)

- **UI-Label**: Symbole (komma-separiert)
- **Beschreibung**: Liste der Symbole, die dieser Account handeln darf (z.B. `EURUSD, GBPUSD, XAUUSD`).
- **Hinweis**: Leer = es wird die globale Watchlist aus den Einstellungen verwendet.
- **Symbole vom Broker laden**: Button lädt die vom Broker angebotenen Instrumente zur Auswahl.

---

### 2.7 Tab: Strategie

*Zu finden unter: Accounts > [Account bearbeiten] > Tab „Strategie“*

#### Strategie-Label (`StrategyLabel`)

- **Beschreibung**: Kurze Bezeichnung (z.B. „Konservativ“, „Aggressiv“), wird im Strategie-Vergleich angezeigt.

#### Custom System-Prompt (`StrategyPrompt`)

- **Beschreibung**: Optionaler Text, der an den Standard-Prompt angehängt wird. Hier kannst du die Handelsstrategie, erlaubte Instrumente und Risikoverhalten für die KI beschreiben.
- **Hinweis**: In der UI kann der Standard-Prompt eingeklappt angezeigt werden.

---

## 3. Multi‑Timeframe‑Bestätigung (`MultiTimeframeSettings`)

*Zu finden unter: Einstellungen (Global)*

### Multi-Timeframe-Filter aktivieren (`Enabled`)

- **Beschreibung**: Prüft den Trend auf einem höheren Timeframe (z.B. 4H) vor jedem Trade.

### Parameter

- **Höherer Timeframe**: z.B. „4H“ oder „1D“.
- **EMA-Periode**: Länge des Trend-Indikators (z.B. 200).

---

## 4. News‑ und Sentiment‑Einstellungen (`NewsSettings`)

*Zu finden unter: Einstellungen (Global) > News & Sentiment*

### News-Sentiment-Analyse aktivieren (`Enabled`)

- **Beschreibung**: Ob Finanznachrichten in die KI-Entscheidung einfließen.

### Parameter

- **Finnhub API Key**: Schlüssel für den News-Provider (kostenlos unter finnhub.io).
- **Max. Headlines**: Anzahl Schlagzeilen pro Symbol.
- **Refresh (Min.)**: Aktualisierungsintervall der News in Minuten.
