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

// ── Trading Engine (Background Service) ────────────────────────────────
builder.Services.AddSingleton<TradingEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TradingEngine>());

// ── ASP.NET Razor Pages ────────────────────────────────────────────────
builder.Services.AddRazorPages();

var app = builder.Build();

// ── Datenbank beim Start erstellen ─────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
    await db.Database.EnsureCreatedAsync();
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

app.MapGet("/api/status", (TradingEngine engine, IBrokerService broker, IRiskManager risk) =>
    Results.Ok(new
    {
        engineRunning = engine.IsRunning,
        brokerConnected = broker.IsConnected,
        killSwitchActive = risk.IsKillSwitchActive
    }));

Log.Information("Claude Trading Bot starting...");
app.Run();
