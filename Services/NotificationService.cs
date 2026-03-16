using System.Text;
using System.Text.Json;
using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

/// <summary>Sendet Benachrichtigungen via Telegram Bot API.</summary>
public class NotificationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<TelegramSettings> _settingsMonitor;
    private readonly ILogger<NotificationService> _logger;

    private TelegramSettings Settings => _settingsMonitor.CurrentValue;

    public NotificationService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<TelegramSettings> settingsMonitor,
        ILogger<NotificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    /// <summary>Prueft ob Telegram konfiguriert ist.</summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(Settings.BotToken) && !string.IsNullOrEmpty(Settings.ChatId);

    /// <summary>Sendet eine Benachrichtigung nach Trade-Ausfuehrung.</summary>
    public async Task SendTradeNotificationAsync(Trade trade)
    {
        if (!IsConfigured || !Settings.NotifyOnTrade)
            return;

        var emoji = trade.Status == TradeStatus.Executed ? "✅" : "❌";
        var actionText = trade.Action == TradeAction.Buy ? "BUY" : "SELL";

        var message = $"""
            {emoji} *{actionText} {trade.Symbol}*
            Menge: {trade.Quantity:F2} Lots @ ${trade.Price:F4}
            Confidence: {trade.ClaudeConfidence:P0}
            Status: {trade.Status}
            {(trade.RealizedPnL.HasValue ? $"PnL: ${trade.RealizedPnL.Value:F2}" : "")}
            _{EscapeMarkdown(Truncate(trade.ClaudeReasoning, 200))}_
            """;

        await SendMessageAsync(message);
    }

    /// <summary>Sendet eine Alert-Nachricht (Kill Switch, grosse Verluste).</summary>
    public async Task SendAlertAsync(string message)
    {
        if (!IsConfigured || !Settings.NotifyOnAlert)
            return;

        await SendMessageAsync($"🚨 *ALERT*\n{EscapeMarkdown(message)}");
    }

    /// <summary>Sendet eine Nachricht wenn eine Position geschlossen wird.</summary>
    public async Task SendPositionClosedAsync(string symbol, string side, decimal pnl)
    {
        if (!IsConfigured || !Settings.NotifyOnTrade)
            return;

        var emoji = pnl >= 0 ? "💰" : "📉";
        var message = $"{emoji} *Position geschlossen: {symbol}*\n" +
                      $"Side: {side.ToUpper()}\n" +
                      $"P&L: {(pnl >= 0 ? "+" : "")}${pnl:F2}";

        await SendMessageAsync(message);
    }

    /// <summary>Sendet eine Nachricht mit beliebigem parse_mode (z.B. "MarkdownV2" fuer Reports).</summary>
    public async Task SendRawMessageAsync(string text, string parseMode = "Markdown")
    {
        if (!IsConfigured) return;
        await SendMessageAsync(text, parseMode);
    }

    private async Task SendMessageAsync(string text, string parseMode = "Markdown")
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var url = $"https://api.telegram.org/bot{Settings.BotToken}/sendMessage";
            var payload = new
            {
                chat_id = Settings.ChatId,
                text = text.Trim(),
                parse_mode = parseMode,
                disable_web_page_preview = true
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Telegram API Fehler: {Status} – {Body}", (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram-Nachricht konnte nicht gesendet werden");
        }
    }

    private static string EscapeMarkdown(string text)
    {
        return text
            .Replace("_", "\\_")
            .Replace("*", "\\*")
            .Replace("[", "\\[")
            .Replace("`", "\\`");
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;
        return text[..maxLength] + "...";
    }
}

public class TelegramSettings
{
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public bool NotifyOnTrade { get; set; } = true;
    public bool NotifyOnAlert { get; set; } = true;
}
