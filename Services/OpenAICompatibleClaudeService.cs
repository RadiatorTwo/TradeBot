using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

/// <summary>
/// LLM-Provider über eine OpenAI-kompatible API (chat/completions).
/// Ermöglicht dasselbe Modell wie in Cursor zu nutzen: z. B. Ollama, LM Studio, OpenRouter
/// oder einen anderen Endpoint, der das OpenAI-Format spricht.
/// </summary>
public class OpenAICompatibleClaudeService : IClaudeService
{
    private readonly HttpClient _http;
    private readonly OpenAICompatibleSettings _settings;
    private readonly ILogger<OpenAICompatibleClaudeService> _logger;

    /// <summary>
    /// Snake_case Policy für OpenAI/Ollama API (max_tokens, nicht maxTokens).
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>CamelCase für Response-Deserialisierung (ClaudeTradeRecommendation etc.)</summary>
    private static readonly JsonSerializerOptions DeserializeOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OpenAICompatibleClaudeService(
        HttpClient http,
        IOptions<OpenAICompatibleSettings> settings,
        ILogger<OpenAICompatibleClaudeService> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;

        var baseUrl = _settings.BaseUrl?.TrimEnd('/') ?? "http://localhost:11434/v1";
        _http.BaseAddress = new Uri(baseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds > 0 ? _settings.TimeoutSeconds : 60);
        if (!string.IsNullOrEmpty(_settings.ApiKey))
            _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + _settings.ApiKey);
    }

    public async Task<ClaudeTradeRecommendation?> AnalyzeAsync(ClaudeAnalysisRequest request, CancellationToken ct = default)
    {
        var userPrompt = ClaudePromptBuilder.BuildUserPrompt(request);

        var body = new Dictionary<string, object>
        {
            ["model"] = _settings.Model,
            ["max_tokens"] = _settings.MaxTokens,
            ["temperature"] = 0.2,
            ["messages"] = new[]
            {
                new { role = "system", content = ClaudePromptBuilder.GetSystemPrompt(request.StrategyPrompt) },
                new { role = "user", content = userPrompt }
            }
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);
        _logger.LogDebug("LLM Request JSON (first 500 chars): {Json}", json.Length > 500 ? json[..500] + "..." : json);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            _logger.LogDebug("Sending analysis request to OpenAI-compatible LLM for {Symbol} (model: {Model})", request.Symbol, _settings.Model);

            var response = await _http.PostAsync("chat/completions", content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                if (statusCode == 404)
                    _logger.LogError("OpenAI-compatible API: Modell '{Model}' nicht gefunden (404). Ist das Modell in Ollama installiert?", _settings.Model);
                else if (statusCode >= 500)
                    _logger.LogError("OpenAI-compatible API: Server-Fehler {Status} (Out of Memory? GPU überlastet?): {Body}", statusCode, responseBody);
                else
                    _logger.LogError("OpenAI-compatible API error {Status}: {Body}", statusCode, responseBody);
                return null;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                _logger.LogWarning("OpenAI-compatible API returned no choices.");
                return null;
            }

            var textContent = choices[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(textContent))
                return null;

            _logger.LogDebug("LLM raw response for {Symbol}: {Content}", request.Symbol, textContent.Length > 500 ? textContent[..500] : textContent);

            var cleanJson = textContent
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();

            var recommendation = JsonSerializer.Deserialize<ClaudeTradeRecommendation>(cleanJson, DeserializeOpts);

            _logger.LogDebug(
                "LLM recommends {Action} {Qty:F2} Lots {Symbol} (confidence: {Conf:P0}, SL: {SL}, TP: {TP})",
                recommendation?.Action, recommendation?.Quantity,
                recommendation?.Symbol, recommendation?.Confidence,
                recommendation?.StopLossPrice, recommendation?.TakeProfitPrice);

            return recommendation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OpenAI-compatible API for {Symbol}", request.Symbol);
            return null;
        }
    }
}
