using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

/// <summary>Wählt zur Laufzeit den konfigurierten LLM-Provider (Anthropic, Gemini, OpenAICompatible).</summary>
public class LlmProviderResolver : IClaudeService
{
    private readonly IClaudeService _impl;

    public LlmProviderResolver(
        IConfiguration configuration,
        ClaudeService anthropicService,
        GeminiClaudeService geminiService,
        OpenAICompatibleClaudeService openAiCompatibleService,
        IOptions<LlmSettings>? llmSettings = null)
    {
        var provider = llmSettings?.Value?.Provider?.Trim()
            ?? configuration["Llm:Provider"]?.Trim()
            ?? "Gemini";

        _impl = provider.Equals("OpenAICompatible", StringComparison.OrdinalIgnoreCase)
            ? openAiCompatibleService
            : provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase)
                ? geminiService
                : anthropicService;
    }

    public Task<ClaudeTradeRecommendation?> AnalyzeAsync(ClaudeAnalysisRequest request, CancellationToken ct = default)
        => _impl.AnalyzeAsync(request, ct);
}
