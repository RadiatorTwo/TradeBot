using ClaudeTradingBot.Components;
using ClaudeTradingBot.Data;
using ClaudeTradingBot.HealthChecks;
using ClaudeTradingBot.Hubs;
using ClaudeTradingBot.Models;
using ClaudeTradingBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Host.UseSerilog();

// ── Konfiguration binden (Infrastruktur/Secrets – bleiben in appsettings.json) ──
builder.Services.Configure<LlmSettings>(builder.Configuration.GetSection("Llm"));
builder.Services.Configure<AnthropicSettings>(builder.Configuration.GetSection("Anthropic"));
builder.Services.Configure<GeminiSettings>(builder.Configuration.GetSection("Gemini"));
builder.Services.Configure<OpenAICompatibleSettings>(builder.Configuration.GetSection("OpenAICompatible"));
builder.Services.Configure<TelegramSettings>(builder.Configuration.GetSection("Telegram"));
builder.Services.Configure<NewsSettings>(builder.Configuration.GetSection("News"));
builder.Services.Configure<ReportSettings>(builder.Configuration.GetSection("Report"));
// Leere Defaults fuer DI – echte Werte kommen per-Account aus DB via MutableOptionsMonitor
builder.Services.Configure<RiskSettings>(_ => { });
builder.Services.Configure<MultiTimeframeSettings>(_ => { });

// ── Datenbank ──────────────────────────────────────────────────────────
// AddDbContextFactory fuer Blazor-Komponenten (kurzlebige Kontexte)
// Registriert auch AddDbContext fuer bestehende scoped Services
builder.Services.AddDbContextFactory<TradingDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("TradingDb")));

// ── Services: LLM-Provider wählbar (Gemini = kostenloser Cloud-Free-Tier, Anthropic, OpenAICompatible) ──
builder.Services.AddHttpClient<ClaudeService>();
builder.Services.AddHttpClient<GeminiClaudeService>();
builder.Services.AddHttpClient<OpenAICompatibleClaudeService>();
builder.Services.AddSingleton<IClaudeService, LlmProviderResolver>();
// ── HttpClient fuer TradeLocker (ohne BaseAddress – wird per Account gesetzt) ──
builder.Services.AddHttpClient(TradeLockerService.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// ── Shared Singletons (alle Accounts teilen sich diese) ─────────────
builder.Services.AddSingleton<TechnicalAnalysisService>();
builder.Services.AddSingleton<TradingSessionService>();
builder.Services.AddSingleton<MarketHoursService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<EconomicCalendarService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EconomicCalendarService>());
builder.Services.AddHttpClient(NewsSentimentService.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://finnhub.io/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<NewsSentimentService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NewsSentimentService>());

// ── Performance-Reports (Telegram, taeglich/woechentlich) ────────────
builder.Services.AddHostedService<ReportService>();

// ── Dynamische Korrelationsmatrix (taeglich aus historischen Preisen) ─
builder.Services.AddSingleton<CorrelationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CorrelationService>());

// ── Backtesting Engine ────────────────────────────────────────────────
builder.Services.AddTransient<BacktestEngine>();

// ── Settings-Repository (DB statt appsettings.json) ─────────────────
builder.Services.AddSingleton<ISettingsRepository, SettingsRepository>();

// ── Multi-Account Manager (erstellt per-Account: Broker, RiskManager, TradingEngine) ──
builder.Services.AddSingleton<AccountManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AccountManager>());

// ── Position Sync Service (synchronisiert DB mit Broker, iteriert ueber alle Accounts) ──
builder.Services.AddHostedService<PositionSyncService>();

// ── Dashboard Broadcast (SignalR-Push alle 3s) ─────────────────────────
builder.Services.AddSingleton<DashboardBroadcastService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DashboardBroadcastService>());

// ── Health Checks ────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<TradeLockerHealthCheck>("tradelocker", tags: new[] { "broker" })
    .AddCheck<LlmHealthCheck>("llm", tags: new[] { "llm" })
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "db" })
    .AddCheck<TradingActivityHealthCheck>("trading-activity", tags: new[] { "activity" });

// ── Blazor Server ──────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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
app.UseAntiforgery();

// ── Blazor + SignalR ────────────────────────────────────────────────────
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapHub<TradingHub>("/tradinghub");

// ── Health Check Endpunkt ──────────────────────────────────────────────
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var result = new
        {
            status = report.Status.ToString(),
            duration = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.TotalMilliseconds
            })
        };
        await context.Response.WriteAsJsonAsync(result);
    }
});

// ── Interne API-Endpunkte (fuer Dashboard-Komponenten) ───────────────────

// ── PnL-History API (fuer Equity-Kurve) ──────────────────────────────────
app.MapGet("/api/pnl-history", async (TradingDbContext db) =>
{
    var history = await db.DailyPnLs
        .OrderBy(d => d.Date)
        .Select(d => new { date = d.Date.ToString("yyyy-MM-dd"), portfolio = d.PortfolioValue, pnl = d.RealizedPnL })
        .ToListAsync();
    return Results.Ok(history);
});

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
