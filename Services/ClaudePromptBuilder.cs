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
        - quantity ist immer in Lots (z. B. 0.01, 0.1, 1.0). Keine Stückzahlen.
        - stopLossPrice und takeProfitPrice sind absolute Preise (z. B. 1.0850 für EURUSD).
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

        sb.AppendLine("## Portfolio-Kontext");
        sb.AppendLine($"**Verfügbares Kapital:** {req.AvailableCash:F2}");
        sb.AppendLine($"**Portfolio-Gesamtwert (Equity):** {req.PortfolioValue:F2}");

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

        sb.AppendLine();
        sb.AppendLine("Analysiere die Daten und gib deine Handelsempfehlung als JSON. quantity in Lots (z. B. 0.01), stopLossPrice und takeProfitPrice als absolute Preise.");
        return sb.ToString();
    }
}
