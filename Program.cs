using ClaudeTradingBot.Components;
using ClaudeTradingBot.Data;
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

// ── Konfiguration binden ───────────────────────────────────────────────
builder.Services.Configure<LlmSettings>(builder.Configuration.GetSection("Llm"));
builder.Services.Configure<AnthropicSettings>(builder.Configuration.GetSection("Anthropic"));
builder.Services.Configure<GeminiSettings>(builder.Configuration.GetSection("Gemini"));
builder.Services.Configure<OpenAICompatibleSettings>(builder.Configuration.GetSection("OpenAICompatible"));
builder.Services.Configure<TradeLockerSettings>(builder.Configuration.GetSection("TradeLocker"));
builder.Services.Configure<RiskSettings>(builder.Configuration.GetSection("RiskManagement"));
builder.Services.Configure<TelegramSettings>(builder.Configuration.GetSection("Telegram"));
builder.Services.Configure<PaperTradingSettings>(builder.Configuration.GetSection("PaperTrading"));
builder.Services.Configure<MultiTimeframeSettings>(builder.Configuration.GetSection("MultiTimeframe"));
builder.Services.Configure<NewsSettings>(builder.Configuration.GetSection("News"));

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

// ── Backtesting Engine ────────────────────────────────────────────────
builder.Services.AddTransient<BacktestEngine>();

// ── Multi-Account Manager (erstellt per-Account: Broker, RiskManager, TradingEngine) ──
builder.Services.AddSingleton<AccountManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AccountManager>());

// Backwards-Kompatibilitaet: Default-Account Services fuer bestehende Injections
builder.Services.AddSingleton<TradingEngine>(sp => sp.GetRequiredService<AccountManager>().DefaultAccount.Engine);
builder.Services.AddSingleton<IBrokerService>(sp => sp.GetRequiredService<AccountManager>().DefaultAccount.EffectiveBroker);
builder.Services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<AccountManager>().DefaultAccount.Risk);
builder.Services.AddSingleton<PaperTradingBrokerDecorator>(sp => sp.GetRequiredService<AccountManager>().DefaultAccount.PaperTrading);

// ── Position Sync Service (synchronisiert DB mit Broker) ───────────────
builder.Services.AddHostedService<PositionSyncService>();

// ── Dashboard Broadcast (SignalR-Push alle 3s) ─────────────────────────
builder.Services.AddSingleton<DashboardBroadcastService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DashboardBroadcastService>());

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
