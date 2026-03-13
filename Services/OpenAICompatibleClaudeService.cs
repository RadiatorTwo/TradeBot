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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
        if (!string.IsNullOrEmpty(_settings.ApiKey))
            _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + _settings.ApiKey);
    }

    public async Task<ClaudeTradeRecommendation?> AnalyzeAsync(ClaudeAnalysisRequest request, CancellationToken ct = default)
    {
        var userPrompt = ClaudePromptBuilder.BuildUserPrompt(request);

        var body = new
        {
            model = _settings.Model,
            max_tokens = _settings.MaxTokens,
            messages = new[]
            {
                new { role = "system", content = ClaudePromptBuilder.SystemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            _logger.LogInformation("Sending analysis request to OpenAI-compatible LLM for {Symbol} (model: {Model})", request.Symbol, _settings.Model);

            var response = await _http.PostAsync("chat/completions", content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("OpenAI-compatible API error {Status}: {Body}", response.StatusCode, responseBody);
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

            var cleanJson = textContent
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();

            var recommendation = JsonSerializer.Deserialize<ClaudeTradeRecommendation>(cleanJson, JsonOpts);

            _logger.LogInformation(
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
