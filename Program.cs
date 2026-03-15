using ClaudeTradingBot.Components;
using ClaudeTradingBot.Data;
using ClaudeTradingBot.HealthChecks;
using ClaudeTradingBot.Hubs;
using ClaudeTradingBot.Models;
using ClaudeTradingBot.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using Prometheus;
using Radzen;
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
builder.Services.AddHttpClient<ClaudeService>()
    .AddStandardResilienceHandler(o => ConfigureLlmResilience(o));
builder.Services.AddHttpClient<GeminiClaudeService>()
    .AddStandardResilienceHandler(o => ConfigureLlmResilience(o));
builder.Services.AddHttpClient<OpenAICompatibleClaudeService>()
    .AddStandardResilienceHandler(o => ConfigureLlmResilience(o));
builder.Services.AddSingleton<IClaudeService, LlmProviderResolver>();
// ── HttpClient fuer TradeLocker (ohne BaseAddress – wird per Account gesetzt) ──
builder.Services.AddHttpClient(TradeLockerService.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddStandardResilienceHandler(o => ConfigureBrokerResilience(o));

// ── Shared Singletons (alle Accounts teilen sich diese) ─────────────
builder.Services.AddSingleton<TechnicalAnalysisService>();
builder.Services.AddSingleton<TradingSessionService>();
builder.Services.AddSingleton<MarketHoursService>();
builder.Services.AddSingleton<ClaudeTradingBot.Services.NotificationService>();
builder.Services.AddSingleton<EconomicCalendarService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EconomicCalendarService>());
builder.Services.AddHttpClient(NewsSentimentService.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://finnhub.io/");
    client.Timeout = TimeSpan.FromSeconds(15);
}).AddStandardResilienceHandler(o => ConfigureNewsResilience(o));
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

// ── Authentifizierung ────────────────────────────────────────────────
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(24);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddRazorPages();

// ── Rate Limiting ────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // API-Endpunkte: max 30 Requests pro Minute pro User
    options.AddPolicy("api", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1)
            }));
    // Export: max 5 pro Minute (schwere DB-Query)
    options.AddPolicy("export", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1)
            }));
});

// ── Blazor Server + Radzen ────────────────────────────────────────────
builder.Services.AddRadzenComponents();
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

    // ── Admin-Seed: Erststart → admin/admin mit erzwungener Passwortaenderung ──
    if (!await db.AppUsers.AnyAsync())
    {
        var adminUser = new AppUser
        {
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.EnhancedHashPassword("admin", 12),
            MustChangePassword = true,
            CreatedAt = DateTime.UtcNow
        };
        db.AppUsers.Add(adminUser);
        await db.SaveChangesAsync();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        logger.LogInformation("Admin-Benutzer angelegt (admin/admin). Passwortaenderung beim ersten Login erzwungen.");
    }

    // ── Daten-Retention: alte Logs loeschen (> 90 Tage) ──
    var retentionCutoff = DateTime.UtcNow.AddDays(-90);
    var deletedLogs = await db.TradingLogs
        .Where(l => l.Timestamp < retentionCutoff)
        .ExecuteDeleteAsync();
    if (deletedLogs > 0)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        logger.LogInformation("{Count} TradingLogs aelter als 90 Tage geloescht", deletedLogs);
    }
}

// ── Middleware ──────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseSerilogRequestLogging();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();
app.UseHttpMetrics(); // Prometheus HTTP-Metriken

// ── Prometheus Metriken ──────────────────────────────────────────────────
app.MapMetrics(); // /metrics Endpunkt (Prometheus-Format)

// ── Razor Pages (Login) ──────────────────────────────────────────────────
app.MapRazorPages().AllowAnonymous();

// ── Logout Endpunkt ─────────────────────────────────────────────────────
app.MapGet("/account/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).AllowAnonymous();

// ── Blazor + SignalR ────────────────────────────────────────────────────
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapHub<TradingHub>("/tradinghub").RequireAuthorization();

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
}).RequireAuthorization().RequireRateLimiting("api");

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
}).RequireAuthorization().RequireRateLimiting("export");

Log.Information("Claude Trading Bot starting...");
app.Run();

// ── Circuit Breaker Konfigurationen ─────────────────────────────────────

static void ConfigureLlmResilience(Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions o)
{
    // LLM-Aufrufe sind langsam – grosszuegige Timeouts
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
    // Retry: 2 Versuche mit exponentiellem Backoff
    o.Retry.MaxRetryAttempts = 2;
    o.Retry.Delay = TimeSpan.FromSeconds(3);
    // Circuit Breaker: nach 5 Fehlern 30s offen (SamplingDuration >= 2x AttemptTimeout)
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
    o.CircuitBreaker.FailureRatio = 0.8;
    o.CircuitBreaker.MinimumThroughput = 5;
    o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
}

static void ConfigureBrokerResilience(Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions o)
{
    // Broker-Aufrufe: schnellere Timeouts, kein automatisches Retry fuer Orders
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
    o.Retry.MaxRetryAttempts = 1;
    o.Retry.Delay = TimeSpan.FromSeconds(2);
    // Circuit Breaker: nach 5 Fehlern 30s offen
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    o.CircuitBreaker.FailureRatio = 0.8;
    o.CircuitBreaker.MinimumThroughput = 5;
    o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
}

static void ConfigureNewsResilience(Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions o)
{
    // News: unkritisch, kurze Timeouts, wenig Retries
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
    o.Retry.MaxRetryAttempts = 1;
    o.Retry.Delay = TimeSpan.FromSeconds(5);
    // Circuit Breaker: nach 3 Fehlern 60s offen (spart API-Quota)
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
    o.CircuitBreaker.FailureRatio = 0.5;
    o.CircuitBreaker.MinimumThroughput = 3;
    o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(60);
}
