using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

/// <summary>
/// Google Gemini API – kostenloser Cloud-Free-Tier (ohne Kreditkarte).
/// Starke Modelle (z. B. Gemini 2.0 Flash), ausreichend für Trading-Analysen.
/// API-Key: https://aistudio.google.com/apikey
/// </summary>
public class GeminiClaudeService : IClaudeService
{
    private readonly HttpClient _http;
    private readonly GeminiSettings _settings;
    private readonly ILogger<GeminiClaudeService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GeminiClaudeService(
        HttpClient http,
        IOptions<GeminiSettings> settings,
        ILogger<GeminiClaudeService> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;

        _http.BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/");
    }

    public async Task<ClaudeTradeRecommendation?> AnalyzeAsync(ClaudeAnalysisRequest request, CancellationToken ct = default)
    {
        var userPrompt = ClaudePromptBuilder.BuildUserPrompt(request);

        var body = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = userPrompt } } }
            },
            systemInstruction = new
            {
                parts = new[] { new { text = ClaudePromptBuilder.GetSystemPrompt(request.StrategyPrompt) } }
            },
            generationConfig = new
            {
                maxOutputTokens = _settings.MaxTokens,
                temperature = 0.2
            }
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var apiKey = _settings.ApiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Gemini:ApiKey ist leer. Bitte in appsettings setzen oder unter https://aistudio.google.com/apikey erzeugen.");
            return null;
        }

        try
        {
            _logger.LogDebug("Sending analysis request to Gemini for {Symbol} (model: {Model})", request.Symbol, _settings.Model);

            var url = $"models/{_settings.Model}:generateContent?key={Uri.EscapeDataString(apiKey)}";
            HttpResponseMessage? response = null;
            var responseBody = "";
            const int maxRetries = 2;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                response = await _http.PostAsync(url, content, ct);
                responseBody = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                    break;

                if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt == maxRetries)
                    break;

                var delayMs = 45_000; // Standard 40s+ Pause bei 429
                if (TryParseRetryAfterSeconds(responseBody, out var retrySec))
                    delayMs = (int)(retrySec * 1000);
                _logger.LogWarning("Gemini 429 (Rate Limit). Warte {Sec}s vor erneutem Versuch.", delayMs / 1000);
                await Task.Delay(Math.Min(delayMs, 60_000), ct);
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                if (responseBody.Contains("limit: 0", StringComparison.OrdinalIgnoreCase))
                    _logger.LogError(
                        "Gemini Free-Tier-Quota ist 0. Bei neuen Konten muss ein Billing-Account verknüpft werden (Free-Tier wird nicht belastet). Siehe https://ai.google.dev/gemini-api/docs/rate-limits. Alternativ: Llm:Provider auf 'Anthropic' stellen.");
                else
                    _logger.LogError("Gemini API error {Status}: {Body}", response?.StatusCode, responseBody);
                return null;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            {
                _logger.LogWarning("Gemini returned no candidates. Response: {Body}", Truncate(responseBody, 300));
                return null;
            }

            var parts = candidates[0].GetProperty("content").GetProperty("parts");
            var textContent = parts.GetArrayLength() > 0
                ? parts[0].GetProperty("text").GetString()
                : null;

            if (string.IsNullOrWhiteSpace(textContent))
                return null;

            var cleanJson = textContent
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();

            var recommendation = JsonSerializer.Deserialize<ClaudeTradeRecommendation>(cleanJson, JsonOpts);

            _logger.LogDebug(
                "Gemini recommends {Action} {Qty:F2} Lots {Symbol} (confidence: {Conf:P0}, SL: {SL}, TP: {TP})",
                recommendation?.Action, recommendation?.Quantity,
                recommendation?.Symbol, recommendation?.Confidence,
                recommendation?.StopLossPrice, recommendation?.TakeProfitPrice);

            return recommendation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Gemini API for {Symbol}", request.Symbol);
            return null;
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static bool TryParseRetryAfterSeconds(string responseBody, out int seconds)
    {
        seconds = 0;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.TryGetProperty("details", out var details))
            {
                foreach (var d in details.EnumerateArray())
                {
                    if (d.TryGetProperty("retryDelay", out var delay))
                    {
                        var s = delay.GetString();
                        if (!string.IsNullOrEmpty(s) && s.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                        {
                            var num = s.TrimEnd('s', 'S');
                            if (double.TryParse(num, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var sec))
                            {
                                seconds = (int)Math.Ceiling(sec);
                                return true;
                            }
                        }
                    }
                }
            }
            var match = System.Text.RegularExpressions.Regex.Match(responseBody, @"retry in (\d+(?:\.\d+)?)\s*s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var retrySec))
            {
                seconds = (int)Math.Ceiling(retrySec);
                return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }
}
