using ClaudeTradingBot.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.HealthChecks;

/// <summary>Prueft ob der konfigurierte LLM-Provider erreichbar ist (einfacher Connectivity-Check).</summary>
public class LlmHealthCheck : IHealthCheck
{
    private readonly IOptionsMonitor<LlmSettings> _llmSettings;
    private readonly IOptionsMonitor<AnthropicSettings> _anthropic;
    private readonly IOptionsMonitor<GeminiSettings> _gemini;
    private readonly IOptionsMonitor<OpenAICompatibleSettings> _openAi;
    private readonly IHttpClientFactory _httpFactory;

    public LlmHealthCheck(
        IOptionsMonitor<LlmSettings> llmSettings,
        IOptionsMonitor<AnthropicSettings> anthropic,
        IOptionsMonitor<GeminiSettings> gemini,
        IOptionsMonitor<OpenAICompatibleSettings> openAi,
        IHttpClientFactory httpFactory)
    {
        _llmSettings = llmSettings;
        _anthropic = anthropic;
        _gemini = gemini;
        _openAi = openAi;
        _httpFactory = httpFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var provider = _llmSettings.CurrentValue.Provider ?? "Gemini";

        try
        {
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var (url, configured) = provider.ToLowerInvariant() switch
            {
                "anthropic" => ("https://api.anthropic.com/", !string.IsNullOrEmpty(_anthropic.CurrentValue.ApiKey)),
                "gemini" => ("https://generativelanguage.googleapis.com/", !string.IsNullOrEmpty(_gemini.CurrentValue.ApiKey)),
                "openaicompatible" => (_openAi.CurrentValue.BaseUrl.TrimEnd('/'), true),
                _ => ("", false)
            };

            if (!configured)
                return HealthCheckResult.Degraded($"LLM Provider '{provider}' nicht konfiguriert (API Key fehlt)");

            if (string.IsNullOrEmpty(url))
                return HealthCheckResult.Degraded($"Unbekannter LLM Provider: {provider}");

            using var response = await client.GetAsync(url, ct);
            // Wir pruefen nur Erreichbarkeit, nicht ob der API-Key gueltig ist
            return HealthCheckResult.Healthy($"LLM Provider '{provider}' erreichbar");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"LLM Provider '{provider}' nicht erreichbar: {ex.Message}");
        }
    }
}
