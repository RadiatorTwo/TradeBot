using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

public interface IClaudeService
{
    Task<ClaudeTradeRecommendation?> AnalyzeAsync(ClaudeAnalysisRequest request, CancellationToken ct = default);
}

public class ClaudeService : IClaudeService
{
    private readonly HttpClient _http;
    private readonly AnthropicSettings _settings;
    private readonly ILogger<ClaudeService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ClaudeService(HttpClient http, IOptions<AnthropicSettings> settings, ILogger<ClaudeService> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            _logger.LogWarning("Anthropic:ApiKey ist leer. Bitte in appsettings oder Umgebungsvariable Anthropic__ApiKey setzen.");
        else if (_settings.ApiKey.TrimStart().StartsWith("crsr_", StringComparison.OrdinalIgnoreCase))
            _logger.LogWarning(
                "Anthropic:ApiKey beginnt mit 'crsr_' – das ist ein Cursor-IDE-Key, kein Anthropic-Key. " +
                "Cursor Agentic Workflow nutzt die Anthropic-API: Trage hier denselben Key ein wie in Cursor (von https://console.anthropic.com/, Format sk-ant-...). " +
                "Alternativ: Llm:Provider auf 'Gemini' oder 'OpenAICompatible' stellen.");

        _http.BaseAddress = new Uri("https://api.anthropic.com/");
        _http.DefaultRequestHeaders.Add("x-api-key", _settings.ApiKey ?? "");
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<ClaudeTradeRecommendation?> AnalyzeAsync(ClaudeAnalysisRequest request, CancellationToken ct = default)
    {
        var prompt = ClaudePromptBuilder.BuildUserPrompt(request);

        var body = new
        {
            model = _settings.Model,
            max_tokens = _settings.MaxTokens,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            system = ClaudePromptBuilder.SystemPrompt
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            _logger.LogDebug("Sending analysis request to Claude for {Symbol}", request.Symbol);

            var response = await _http.PostAsync("v1/messages", content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                if (responseBody.Contains("credit balance", StringComparison.OrdinalIgnoreCase)
                    || responseBody.Contains("Plans & Billing", StringComparison.OrdinalIgnoreCase))
                    _logger.LogError(
                        "Anthropic API: Guthaben zu niedrig. API erfordert Credits/Kreditkarte (console.anthropic.com → Plans & Billing). " +
                        "Kostenlos: Llm:Provider auf 'Gemini' stellen und in Google Cloud Billing verknüpfen (Free-Tier wird nicht belastet).");
                else
                    _logger.LogError("Claude API error {Status}: {Body}", response.StatusCode, responseBody);
                return null;
            }

            // Parse Claude response
            using var doc = JsonDocument.Parse(responseBody);
            var textContent = doc.RootElement
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(textContent))
                return null;

            // Claude könnte JSON in ```json ... ``` wrappen
            var cleanJson = textContent
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();

            var recommendation = JsonSerializer.Deserialize<ClaudeTradeRecommendation>(cleanJson, JsonOpts);

            _logger.LogDebug(
                "Claude recommends {Action} {Qty:F2} Lots {Symbol} (confidence: {Conf:P0}, SL: {SL}, TP: {TP})",
                recommendation?.Action, recommendation?.Quantity,
                recommendation?.Symbol, recommendation?.Confidence,
                recommendation?.StopLossPrice, recommendation?.TakeProfitPrice);

            return recommendation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Claude API for {Symbol}", request.Symbol);
            return null;
        }
    }

}
