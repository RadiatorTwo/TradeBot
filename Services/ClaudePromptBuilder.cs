using System.Text;
using ClaudeTradingBot.Models;

namespace ClaudeTradingBot.Services;

/// <summary>Gemeinsame Prompt-Bausteine für alle LLM-Provider (Anthropic, OpenAI-kompatibel/Cursor).</summary>
public static class ClaudePromptBuilder
{
    public const string SystemPrompt = """
        Du bist ein quantitativer Trading-Analyst für Forex und CFDs (z. B. EURUSD, GBPUSD, XAUUSD, US100).
        Du analysierst Kurse, Candles und Kontodaten und gibst strukturierte Handelsempfehlungen.
        Antworte IMMER ausschließlich als valides JSON-Objekt mit folgender Struktur:
        {
          "symbol": "SYMBOL",
          "action": "buy" | "sell" | "hold",
          "quantity": <Lots als Dezimal, z.B. 0.01 für 1 Micro-Lot>,
          "confidence": <0.0 bis 1.0>,
          "reasoning": "Kurze Begründung",
          "stopLossPrice": <Preis für Stop-Loss oder null>,
          "takeProfitPrice": <Preis für Take-Profit oder null>
        }

        WICHTIGE REGELN FÜR POSITIONSGRÖSSE:
        - 1 Standard-Lot Forex = 100.000 Einheiten der Basiswährung (~100.000 $ Margin bei 1:1 Hebel).
        - Nutze MAXIMAL 5-10% des verfügbaren Kapitals pro Trade.
        - Bei einem Konto mit 25.000 $ bedeutet das max. 0.02 Lots (2 Micro-Lots).
        - Berechnung: (Kapital × MaxRisiko%) / (100.000 × Preis) = Lots. Runde auf 0.01 ab.
        - stopLossPrice und takeProfitPrice sind absolute Preise (z. B. 1.0850 für EURUSD).
        - Setze IMMER stopLossPrice und takeProfitPrice wenn action != "hold".

        Kein Markdown, keine Erklärung, kein Text außerhalb des JSON.
        """;

    public static string BuildUserPrompt(ClaudeAnalysisRequest req)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Marktdaten (Forex/CFD)");
        sb.AppendLine();
        sb.AppendLine($"**Symbol:** {req.Symbol}");
        sb.AppendLine($"**Aktueller Preis (Mid):** {req.CurrentPrice:F4}");
        if (req.Bid != 0 || req.Ask != 0)
        {
            sb.AppendLine($"**Bid:** {req.Bid:F4}  **Ask:** {req.Ask:F4}");
            var spreadPips = PipCalculator.PriceToPips(req.Symbol, req.Ask - req.Bid);
            sb.AppendLine($"**Aktueller Spread:** {spreadPips:F1} Pips");
        }
        sb.AppendLine($"**Tagesveränderung:** {req.DayChange:+0.00;-0.00}%");
        if (req.Volume > 0)
            sb.AppendLine($"**Volumen:** {req.Volume:N0}");
        sb.AppendLine();

        if (req.RecentPrices.Count > 0)
        {
            sb.AppendLine("**Letzte Kurse 1H (neueste zuerst):**");
            sb.AppendLine(string.Join(", ", req.RecentPrices.Select(p => p.ToString("F4"))));
            sb.AppendLine();
        }

        if (req.Candles1D.Count > 0)
        {
            sb.AppendLine("**Candles 1D (Close):**");
            sb.AppendLine(string.Join(", ", req.Candles1D.Select(p => p.ToString("F4"))));
            sb.AppendLine();
        }
        if (req.Candles4H.Count > 0)
        {
            sb.AppendLine("**Candles 4H (Close):**");
            sb.AppendLine(string.Join(", ", req.Candles4H.Select(p => p.ToString("F4"))));
            sb.AppendLine();
        }
        if (req.Candles1H.Count > 0)
        {
            sb.AppendLine("**Candles 1H (Close):**");
            sb.AppendLine(string.Join(", ", req.Candles1H.Select(p => p.ToString("F4"))));
            sb.AppendLine();
        }

        if (req.Indicators != null)
        {
            sb.AppendLine("## Technische Indikatoren");
            sb.AppendLine();

            if (req.Indicators.RSI14.HasValue)
                sb.AppendLine($"**RSI(14):** {req.Indicators.RSI14:F2} {GetRSISignal(req.Indicators.RSI14.Value)}");

            if (req.Indicators.EMA20.HasValue || req.Indicators.EMA50.HasValue || req.Indicators.EMA200.HasValue)
            {
                sb.AppendLine("**EMA:**");
                if (req.Indicators.EMA20.HasValue)
                    sb.AppendLine($"  - EMA(20): {req.Indicators.EMA20:F4}");
                if (req.Indicators.EMA50.HasValue)
                    sb.AppendLine($"  - EMA(50): {req.Indicators.EMA50:F4}");
                if (req.Indicators.EMA200.HasValue)
                    sb.AppendLine($"  - EMA(200): {req.Indicators.EMA200:F4}");

                // EMA-Kreuzungen beschreiben
                if (req.Indicators.EMA20.HasValue && req.Indicators.EMA50.HasValue)
                {
                    var emaSignal = req.Indicators.EMA20.Value > req.Indicators.EMA50.Value
                        ? "EMA20 > EMA50 (bullisch)"
                        : "EMA20 < EMA50 (bärisch)";
                    sb.AppendLine($"  - Signal: {emaSignal}");
                }
            }

            if (req.Indicators.MACDLine.HasValue)
            {
                sb.AppendLine($"**MACD(12,26,9):**");
                sb.AppendLine($"  - Linie: {req.Indicators.MACDLine:F6}");
                if (req.Indicators.MACDSignal.HasValue)
                    sb.AppendLine($"  - Signal: {req.Indicators.MACDSignal:F6}");
                if (req.Indicators.MACDHistogram.HasValue)
                {
                    var histSignal = req.Indicators.MACDHistogram.Value > 0 ? "bullisch" : "bärisch";
                    sb.AppendLine($"  - Histogramm: {req.Indicators.MACDHistogram:F6} ({histSignal})");
                }
            }

            if (req.Indicators.ATR14.HasValue)
                sb.AppendLine($"**ATR(14):** {req.Indicators.ATR14:F4} (Volatilitaet)");

            if (req.Indicators.BollingerUpper.HasValue)
            {
                sb.AppendLine($"**Bollinger Bands(20,2):**");
                sb.AppendLine($"  - Oberes Band: {req.Indicators.BollingerUpper:F4}");
                sb.AppendLine($"  - Mittleres Band: {req.Indicators.BollingerMiddle:F4}");
                sb.AppendLine($"  - Unteres Band: {req.Indicators.BollingerLower:F4}");

                if (req.CurrentPrice > 0 && req.Indicators.BollingerLower.HasValue)
                {
                    string bbPosition;
                    if (req.CurrentPrice > req.Indicators.BollingerUpper!.Value)
                        bbPosition = "Preis ÜBER oberem Band (überkauft/starker Trend)";
                    else if (req.CurrentPrice < req.Indicators.BollingerLower.Value)
                        bbPosition = "Preis UNTER unterem Band (überverkauft/starker Abwärtstrend)";
                    else
                        bbPosition = "Preis innerhalb der Bänder";
                    sb.AppendLine($"  - Position: {bbPosition}");
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Portfolio-Kontext");
        sb.AppendLine($"**Verfügbares Kapital:** ${req.AvailableCash:F2}");
        sb.AppendLine($"**Portfolio-Gesamtwert (Equity):** ${req.PortfolioValue:F2}");
        sb.AppendLine("**Max. Positionsgröße:** 10% des Kapitals pro Trade");

        if (req.CurrentPosition != null)
        {
            sb.AppendLine();
            sb.AppendLine("**Bestehende Position:**");
            sb.AppendLine($"- Menge (Lots): {req.CurrentPosition.Quantity}");
            sb.AppendLine($"- Durchschnittspreis: {req.CurrentPosition.AveragePrice:F4}");
            sb.AppendLine($"- Unrealisierter P&L: {req.CurrentPosition.UnrealizedPnL:F2} ({req.CurrentPosition.UnrealizedPnLPercent:+0.00;-0.00}%)");
        }
        else
        {
            sb.AppendLine("**Bestehende Position:** Keine");
        }

        // ── Trade-Historie (Feedback-Loop) ────────────────────────────────
        if (req.RecentTradeResults.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Deine bisherigen Empfehlungen für dieses Symbol");
            sb.AppendLine();

            var wins = req.RecentTradeResults.Count(t => t.RealizedPnL > 0);
            var total = req.RecentTradeResults.Count;
            var winRate = total > 0 ? (double)wins / total * 100 : 0;
            var totalPnL = req.RecentTradeResults.Sum(t => t.RealizedPnL);

            sb.AppendLine($"**Letzte {total} geschlossene Trades – Win-Rate: {winRate:F0}%, Gesamt-PnL: {totalPnL:+0.00;-0.00} $**");
            sb.AppendLine();

            foreach (var t in req.RecentTradeResults)
            {
                var pnlSign = t.RealizedPnL >= 0 ? "+" : "";
                sb.AppendLine($"- {t.ClosedAt:dd.MM.yyyy HH:mm} | {t.Action.ToUpper()} @ {t.EntryPrice:F4} → {t.ExitPrice:F4} | PnL: {pnlSign}{t.RealizedPnL:F2} $ | Confidence: {t.Confidence:P0}");
            }

            sb.AppendLine();
            sb.AppendLine("Berücksichtige diese Ergebnisse bei deiner Analyse. Wenn du Muster in deinen Fehlern erkennst, passe deine Strategie an.");
        }

        sb.AppendLine();
        sb.AppendLine("Analysiere die Daten und gib deine Handelsempfehlung als JSON. quantity in Lots (z. B. 0.01), stopLossPrice und takeProfitPrice als absolute Preise.");
        return sb.ToString();
    }

    private static string GetRSISignal(decimal rsi) => rsi switch
    {
        >= 70 => "(überkauft – Verkaufssignal)",
        >= 60 => "(leicht überkauft)",
        <= 30 => "(überverkauft – Kaufsignal)",
        <= 40 => "(leicht überverkauft)",
        _ => "(neutral)"
    };
}
