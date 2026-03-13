using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClaudeTradingBot.Pages;

public class SettingsModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _env;

    public SettingsModel(IConfiguration configuration, IWebHostEnvironment env)
    {
        _configuration = configuration;
        _env = env;
    }

    [BindProperty]
    public string WatchList { get; set; } = string.Empty;

    [BindProperty]
    public double MinConfidence { get; set; }

    [BindProperty]
    public double MaxPositionSizePercent { get; set; }

    [BindProperty]
    public double MaxDailyLossPercent { get; set; }

    [BindProperty]
    public double StopLossPercent { get; set; }

    [BindProperty]
    public int MaxOpenPositions { get; set; }

    [BindProperty]
    public int TradingIntervalMinutes { get; set; }

    [BindProperty]
    public decimal MaxDailyLossAbsolute { get; set; }

    public string LlmProvider { get; set; } = string.Empty;
    public string AnthropicKeyMasked { get; set; } = string.Empty;
    public string GeminiKeyMasked { get; set; } = string.Empty;
    public string OpenAIKeyMasked { get; set; } = string.Empty;
    public string TradeLockerEmail { get; set; } = string.Empty;

    public bool SaveSuccess { get; set; }

    public void OnGet()
    {
        LoadCurrentValues();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (MinConfidence < 0 || MinConfidence > 1)
            ModelState.AddModelError(nameof(MinConfidence), "Min. Confidence muss zwischen 0 und 1 liegen.");
        if (MaxPositionSizePercent <= 0)
            ModelState.AddModelError(nameof(MaxPositionSizePercent), "Max. Positionsgröße muss > 0 sein.");
        if (MaxDailyLossPercent <= 0)
            ModelState.AddModelError(nameof(MaxDailyLossPercent), "Max. Tagesverlust muss > 0 sein.");
        if (StopLossPercent <= 0)
            ModelState.AddModelError(nameof(StopLossPercent), "Stop-Loss muss > 0 sein.");
        if (MaxOpenPositions < 1)
            ModelState.AddModelError(nameof(MaxOpenPositions), "Max. offene Positionen muss >= 1 sein.");
        if (TradingIntervalMinutes < 1)
            ModelState.AddModelError(nameof(TradingIntervalMinutes), "Trading-Intervall muss >= 1 Minute sein.");
        if (MaxDailyLossAbsolute <= 0)
            ModelState.AddModelError(nameof(MaxDailyLossAbsolute), "Max. Tagesverlust (absolut) muss > 0 sein.");

        if (!ModelState.IsValid)
        {
            LoadCurrentValues();
            return Page();
        }

        var appSettingsPath = Path.Combine(_env.ContentRootPath, "appsettings.json");
        var json = await System.IO.File.ReadAllTextAsync(appSettingsPath);
        var doc = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!;

        // WatchList
        var watchItems = WatchList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var strategyNode = doc["TradingStrategy"] ??= new JsonObject();
        strategyNode["WatchList"] = new JsonArray(watchItems.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray());

        // RiskManagement
        var riskNode = doc["RiskManagement"] ??= new JsonObject();
        riskNode["MinConfidence"] = MinConfidence;
        riskNode["MaxPositionSizePercent"] = MaxPositionSizePercent;
        riskNode["MaxDailyLossPercent"] = MaxDailyLossPercent;
        riskNode["StopLossPercent"] = StopLossPercent;
        riskNode["MaxOpenPositions"] = MaxOpenPositions;
        riskNode["TradingIntervalMinutes"] = TradingIntervalMinutes;
        riskNode["MaxDailyLossAbsolute"] = MaxDailyLossAbsolute;

        var options = new JsonSerializerOptions { WriteIndented = true };
        await System.IO.File.WriteAllTextAsync(appSettingsPath, doc.ToJsonString(options));

        SaveSuccess = true;
        LoadCurrentValues();
        return Page();
    }

    private void LoadCurrentValues()
    {
        var watchArray = _configuration.GetSection("TradingStrategy:WatchList").Get<string[]>() ?? Array.Empty<string>();
        WatchList = string.Join(", ", watchArray);

        MinConfidence = _configuration.GetValue("RiskManagement:MinConfidence", 0.65);
        MaxPositionSizePercent = _configuration.GetValue("RiskManagement:MaxPositionSizePercent", 10.0);
        MaxDailyLossPercent = _configuration.GetValue("RiskManagement:MaxDailyLossPercent", 3.0);
        StopLossPercent = _configuration.GetValue("RiskManagement:StopLossPercent", 5.0);
        MaxOpenPositions = _configuration.GetValue("RiskManagement:MaxOpenPositions", 10);
        TradingIntervalMinutes = _configuration.GetValue("RiskManagement:TradingIntervalMinutes", 15);
        MaxDailyLossAbsolute = _configuration.GetValue("RiskManagement:MaxDailyLossAbsolute", 500m);

        LlmProvider = _configuration.GetValue<string>("Llm:Provider") ?? "Gemini";
        AnthropicKeyMasked = MaskKey(_configuration.GetValue<string>("Anthropic:ApiKey"));
        GeminiKeyMasked = MaskKey(_configuration.GetValue<string>("Gemini:ApiKey"));
        OpenAIKeyMasked = MaskKey(_configuration.GetValue<string>("OpenAICompatible:ApiKey"));
        TradeLockerEmail = _configuration.GetValue<string>("TradeLocker:Email") ?? string.Empty;
    }

    private static string MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return "(nicht gesetzt)";
        if (key.Length <= 8)
            return "***";
        return key[..4] + "***" + key[^4..];
    }
}
