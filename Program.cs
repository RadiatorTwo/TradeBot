using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using ClaudeTradingBot.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Host.UseSerilog();

// ── Konfiguration binden ───────────────────────────────────────────────
builder.Services.Configure<LlmSettings>(builder.Configuration.GetSection("Llm"));
builder.Services.Configure<AnthropicSettings>(builder.Configuration.GetSection("Anthropic"));
builder.Services.Configure<GeminiSettings>(builder.Configuration.GetSection("Gemini"));
builder.Services.Configure<OpenAICompatibleSettings>(builder.Configuration.GetSection("OpenAICompatible"));
builder.Services.Configure<TradeLockerSettings>(builder.Configuration.GetSection("TradeLocker"));
builder.Services.Configure<RiskSettings>(builder.Configuration.GetSection("RiskManagement"));

// ── Datenbank ──────────────────────────────────────────────────────────
builder.Services.AddDbContext<TradingDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("TradingDb")));

// ── Services: LLM-Provider wählbar (Gemini = kostenloser Cloud-Free-Tier, Anthropic, OpenAICompatible) ──
builder.Services.AddHttpClient<ClaudeService>();
builder.Services.AddHttpClient<GeminiClaudeService>();
builder.Services.AddHttpClient<OpenAICompatibleClaudeService>();
builder.Services.AddSingleton<IClaudeService, LlmProviderResolver>();
// Ein einziger TradeLockerService (Singleton), damit Engine und Dashboard dieselbe Instanz nutzen (Balance, Verbindung).
builder.Services.AddHttpClient(TradeLockerService.HttpClientName, (sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TradeLockerSettings>>().Value;
    var baseUrl = (options.BaseUrl ?? "").TrimEnd('/');
    if (!string.IsNullOrEmpty(baseUrl))
        client.BaseAddress = new Uri(baseUrl + "/");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<TradeLockerService>();
builder.Services.AddSingleton<IBrokerService>(sp => sp.GetRequiredService<TradeLockerService>());
builder.Services.AddSingleton<IRiskManager, RiskManager>();
builder.Services.AddSingleton<TechnicalAnalysisService>();
builder.Services.AddSingleton<TradingSessionService>();
builder.Services.AddSingleton<MarketHoursService>();
builder.Services.AddSingleton<EconomicCalendarService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EconomicCalendarService>());

// ── Trading Engine (Background Service) ────────────────────────────────
builder.Services.AddSingleton<TradingEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TradingEngine>());

// ── Position Sync Service (synchronisiert DB mit Broker) ───────────────
builder.Services.AddHostedService<PositionSyncService>();

// ── ASP.NET Razor Pages ────────────────────────────────────────────────
builder.Services.AddRazorPages();

var app = builder.Build();

// ── Datenbank beim Start erstellen ─────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
    await db.Database.MigrateAsync();

    // Einmalig: alte "rejected" Trades von Failed(2) auf Rejected(4) migrieren
    var rejectedCount = await db.Trades
        .Where(t => t.Status == TradeStatus.Failed && t.ErrorMessage != null && t.ErrorMessage.Contains("rejected"))
        .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TradeStatus.Rejected));
    if (rejectedCount > 0)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        logger.LogInformation("{Count} alte Trades von 'Failed' auf 'Rejected' migriert", rejectedCount);
    }
}

// ── Middleware ──────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

// ── API-Endpunkte für Dashboard-Steuerung ──────────────────────────────
app.MapPost("/api/engine/pause", (TradingEngine engine) =>
{
    engine.Pause();
    return Results.Ok(new { status = "paused" });
});

app.MapPost("/api/engine/resume", (TradingEngine engine) =>
{
    engine.Resume();
    return Results.Ok(new { status = "running" });
});

app.MapPost("/api/killswitch/activate", (IRiskManager risk) =>
{
    risk.ActivateKillSwitch("Manually activated via dashboard");
    return Results.Ok(new { status = "activated" });
});

app.MapPost("/api/killswitch/reset", (IRiskManager risk) =>
{
    risk.ResetKillSwitch();
    return Results.Ok(new { status = "reset" });
});

app.MapGet("/api/status", (TradingEngine engine, IBrokerService broker, IRiskManager risk, MarketHoursService marketHours) =>
    Results.Ok(new
    {
        engineRunning = engine.IsRunning,
        brokerConnected = broker.IsConnected,
        killSwitchActive = risk.IsKillSwitchActive,
        marketOpen = marketHours.IsMarketOpen(),
        marketStatus = marketHours.GetMarketStatus()
    }));

// ── CSV-Export für Trade-Historie ────────────────────────────────────────
app.MapGet("/api/trades/export", async (
    TradingDbContext db,
    string? from, string? to, string? symbol, string? status) =>
{
    var query = db.Trades.AsQueryable();

    if (DateTime.TryParse(from, out var fromDate))
        query = query.Where(t => t.CreatedAt >= fromDate);
    if (DateTime.TryParse(to, out var toDate))
        query = query.Where(t => t.CreatedAt <= toDate.Date.AddDays(1));
    if (!string.IsNullOrEmpty(symbol))
        query = query.Where(t => t.Symbol == symbol);
    if (!string.IsNullOrEmpty(status) && Enum.TryParse<TradeStatus>(status, out var s))
        query = query.Where(t => t.Status == s);

    var trades = await query.OrderByDescending(t => t.CreatedAt).Take(2000).ToListAsync();

    static string CsvEscape(string value)
    {
        if (value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("Zeit;Symbol;Aktion;Menge;Preis;Confidence;Status;Begruendung");
    foreach (var t in trades)
    {
        sb.AppendLine($"{t.CreatedAt:dd.MM.yyyy HH:mm:ss};{t.Symbol};{t.Action};{t.Quantity:F2};{t.Price:F4};{t.ClaudeConfidence:P0};{t.Status};{CsvEscape(t.ClaudeReasoning)}");
    }

    var fileName = $"trades-{DateTime.UtcNow:yyyy-MM-dd}.csv";
    return Results.File(
        System.Text.Encoding.UTF8.GetBytes(sb.ToString()),
        "text/csv",
        fileName);
});

Log.Information("Claude Trading Bot starting...");
app.Run();
