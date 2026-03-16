using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeTradingBot.Models;
using Microsoft.Extensions.Options;

namespace ClaudeTradingBot.Services;

/// <summary>
/// TradeLocker-Anbindung: Auth, Marktdaten (Instrumente, Quotes, History),
/// Konto (Details, Positionen) und IBrokerService-Abbildung.
/// </summary>
public partial class TradeLockerService : IBrokerService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public const string HttpClientName = "TradeLocker";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TradeLockerSettings _settings;
    private readonly ILogger<TradeLockerService> _logger;

    private string? _accessToken;
    private string? _refreshToken;
    private string? _accountNumber;
    private string? _accountId;
    private decimal _balanceFromAllAccounts; // Fallback aus all-accounts, wenn /details fehlschlägt
    private bool _isConnected;
    private DateTime _tokenExpiresUtc = DateTime.MinValue;

    /// <summary>Symbol (z. B. EURUSD) → tradableInstrumentId</summary>
    private readonly Dictionary<string, int> _symbolToInstrumentId = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>tradableInstrumentId → Symbol (für Position-Mapping)</summary>
    private readonly Dictionary<int, string> _instrumentIdToSymbol = new();
    /// <summary>tradableInstrumentId → INFO routeId (für Quotes/History)</summary>
    private readonly Dictionary<int, int> _instrumentInfoRouteId = new();
    /// <summary>tradableInstrumentId → TRADE routeId (für Orders)</summary>
    private readonly Dictionary<int, int> _instrumentTradeRouteId = new();

    // ── Globales Rate-Limit für ALLE TradeLocker-Aufrufe (prozessweit) ──
    // Viele Services (TradingEngine, PositionSync, GridTrading, Dashboard) rufen parallel auf.
    // Das Broker-Limit gilt aber in der Regel pro Account/API-Key, nicht pro Service-Instanz.
    // Daher hier ein globaler Throttle über alle TradeLockerService-Instanzen.
    private static readonly SemaphoreSlim GlobalThrottle = new(1, 1);
    private static DateTime _globalLastRequestUtc = DateTime.MinValue;
    // Standard-Broker-Limit (Fallback): ca. 0,3 Requests/Sekunde pro Prozess.
    // Wird nach Login dynamisch über /trade/config angepasst, falls die API Limits liefert.
    private static int _globalMinDelayMs = 3000;

    // ── Caching ────────────────────────────────────────────────────────────
    private AccountDetails? _cachedAccountDetails;
    private List<Position>? _cachedPositions;
    private DateTime _accountDetailsCacheExpiry = DateTime.MinValue;
    private DateTime _positionsCacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    /// <summary>true wenn /details einmal 404 geliefert hat – wird nie wieder versucht.</summary>
    private bool _detailsEndpointIs404;

    // ── Auto-Reconnect (Phase 5.4) ──────────────────────────────────────
    private int _reconnectAttempt;
    private static readonly int[] ReconnectBackoffSeconds = { 5, 10, 30, 60 };
    private const int MaxReconnectAttempts = 10;

    public bool IsConnected => _isConnected;

    public TradeLockerService(
        IHttpClientFactory httpClientFactory,
        IOptions<TradeLockerSettings> settings,
        ILogger<TradeLockerService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>Konstruktor fuer Multi-Account (Phase 7.1) – Settings direkt uebergeben.</summary>
    internal TradeLockerService(
        IHttpClientFactory httpClientFactory,
        TradeLockerSettings settings,
        ILogger<TradeLockerService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Erzeugt einen pro Request konfigurierten HttpClient (Auth + accNum). Nicht speichern, nach Verwendung verwerfen.</summary>
    private HttpClient GetHttpClient()
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        // BaseAddress setzen falls nicht vom Factory konfiguriert (Multi-Account)
        if (client.BaseAddress == null && !string.IsNullOrEmpty(_settings.BaseUrl))
        {
            var baseUrl = _settings.BaseUrl.TrimEnd('/');
            client.BaseAddress = new Uri(baseUrl + "/");
        }
        if (!string.IsNullOrEmpty(_accessToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (!string.IsNullOrEmpty(_accountNumber))
        {
            client.DefaultRequestHeaders.Remove("accNum");
            client.DefaultRequestHeaders.Add("accNum", _accountNumber);
        }
        return client;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_isConnected)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.Email) ||
            string.IsNullOrWhiteSpace(_settings.Password) ||
            string.IsNullOrWhiteSpace(_settings.Server))
        {
            _logger.LogWarning("TradeLocker credentials are not configured. Please set TradeLocker:Email, :Password and :Server in appsettings or environment.");
            return;
        }

        try
        {
            _logger.LogInformation("Logging in to TradeLocker (server={Server})", _settings.Server);

            var loginBody = new
            {
                email = _settings.Email,
                password = _settings.Password,
                server = _settings.Server
            };

            // Volle URL explizit bauen, damit sicher .../backend-api/auth/jwt/token getroffen wird
            var baseUrl = _settings.BaseUrl?.TrimEnd('/') ?? "";
            var authUrl = string.IsNullOrEmpty(baseUrl) ? "auth/jwt/token" : $"{baseUrl}/auth/jwt/token";
            _logger.LogDebug("Auth request URL: {AuthUrl}", authUrl);

            using var client = GetHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, authUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = JsonContent.Create(loginBody);

            var response = await client.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect)
            {
                var location = response.Headers.Location?.ToString() ?? "(nicht gesetzt)";
                _logger.LogError("TradeLocker login: Server leitet um ({StatusCode}) nach {Location}. " +
                    "Für Ihren Broker (z. B. HeroFX) ggf. diese URL als BaseUrl in appsettings eintragen oder API-Dokumentation des Brokers prüfen.",
                    (int)response.StatusCode, location);
                _isConnected = false;
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("TradeLocker login failed with status {StatusCode}: {ReasonPhrase}. Body: {Body}",
                    (int)response.StatusCode, response.ReasonPhrase, Truncate(errBody, 500));
                _isConnected = false;
                return;
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(responseBody) || responseBody.TrimStart().StartsWith('<'))
            {
                _logger.LogError("TradeLocker login returned HTML or empty response instead of JSON. " +
                    "Prüfen Sie BaseUrl und Server (z.B. HEROFX). Response: {Body}", Truncate(responseBody, 500));
                _isConnected = false;
                return;
            }

            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            TradeLockerAuthResponse? auth;
            try
            {
                auth = System.Text.Json.JsonSerializer.Deserialize<TradeLockerAuthResponse>(responseBody, jsonOptions);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogError(ex, "TradeLocker login: Antwort ist kein gültiges JSON. Response: {Body}", Truncate(responseBody, 500));
                _isConnected = false;
                return;
            }

            if (auth is null || string.IsNullOrWhiteSpace(auth.AccessToken))
            {
                _logger.LogError("TradeLocker login returned empty token response. Response: {Body}", Truncate(responseBody, 300));
                _isConnected = false;
                return;
            }

            _accessToken = auth.AccessToken;
            _refreshToken = auth.RefreshToken;
            _tokenExpiresUtc = DateTime.UtcNow.AddMinutes(15);

            // Accounts abrufen, um accNum zu bestimmen (GetHttpClient setzt jetzt Auth + accNum)
            using (var accClient = GetHttpClient())
            {
                var accountsResponse = await accClient.GetAsync("auth/jwt/all-accounts", ct);

                // Bei 302 Redirect manuell folgen (AllowAutoRedirect ist deaktiviert)
                if (accountsResponse.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently
                    or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
                    && accountsResponse.Headers.Location is { } accountsRedirectLocation)
                {
                    var redirectUrl = accountsRedirectLocation.IsAbsoluteUri
                        ? accountsRedirectLocation.ToString()
                        : $"{baseUrl.TrimEnd('/')}/{accountsRedirectLocation.ToString().TrimStart('/')}";
                    _logger.LogDebug("Following redirect to all-accounts: {Url}", redirectUrl);
                    accountsResponse.Dispose();
                    accountsResponse = await accClient.GetAsync(redirectUrl, ct);
                }

                if (!accountsResponse.IsSuccessStatusCode)
                {
                    var errBody = await accountsResponse.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Failed to retrieve TradeLocker accounts. Status {StatusCode}. Response: {Body}",
                        (int)accountsResponse.StatusCode, Truncate(errBody, 300));
                }
                else
                {
                    var accountsBody = await accountsResponse.Content.ReadAsStringAsync(ct);
                    List<TradeLockerAccountInfo>? accounts = null;
                    if (!string.IsNullOrWhiteSpace(accountsBody) && !accountsBody.TrimStart().StartsWith('<'))
                    {
                        try
                        {
                            // TradeLocker liefert { "accounts": [ { "id", "accNum", "name", ... } ] }
                            using var doc = System.Text.Json.JsonDocument.Parse(accountsBody);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("accounts", out var arr))
                                accounts = ParseAccountArray(arr, jsonOptions);
                            else if (root.TryGetProperty("data", out var data))
                                accounts = ParseAccountArray(data, jsonOptions);
                            if (accounts == null || accounts.Count == 0)
                                accounts = System.Text.Json.JsonSerializer.Deserialize<List<TradeLockerAccountInfo>>(accountsBody, jsonOptions);
                        }
                        catch (System.Text.Json.JsonException ex)
                        {
                            _logger.LogWarning(ex, "TradeLocker all-accounts response was not valid JSON. Body: {Body}", Truncate(accountsBody, 400));
                        }
                    }
                    // Account auswaehlen: konfiguriertes AccountId bevorzugen, sonst ersten nehmen
                    TradeLockerAccountInfo? account = null;
                    if (!string.IsNullOrWhiteSpace(_settings.AccountId) && accounts != null)
                    {
                        var targetId = _settings.AccountId.Trim();
                        account = accounts.FirstOrDefault(a =>
                            a.Id == targetId ||
                            a.Id == targetId.TrimStart('D', '#') ||
                            a.Name.Contains(targetId, StringComparison.OrdinalIgnoreCase));
                    }
                    account ??= accounts?.FirstOrDefault();

                    if (account != null && !string.IsNullOrWhiteSpace(account.Id))
                    {
                        _accountId = account.Id;
                        _accountNumber = !string.IsNullOrWhiteSpace(account.AccNum) ? account.AccNum : "1";
                        _balanceFromAllAccounts = account.AccountBalance;
                        _logger.LogInformation("TradeLocker login successful. Using account {AccountId} (accNum={AccNum}), Balance: {Balance}",
                            account.Id, _accountNumber, _balanceFromAllAccounts);

                        // Andere verfuegbare Accounts loggen
                        if (accounts != null && accounts.Count > 1)
                        {
                            var others = accounts.Where(a => a.Id != account.Id).Select(a => $"{a.Id} ({a.Name})");
                            _logger.LogInformation("TradeLocker: {Count} weitere Accounts verfuegbar: {Others}",
                                accounts.Count - 1, string.Join(", ", others));
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(_settings.AccountId))
                    {
                        _accountId = _settings.AccountId.Trim();
                        _accountNumber = "1";
                        _balanceFromAllAccounts = 0m;
                        _logger.LogInformation("TradeLocker using configured AccountId {AccountId} (not found in all-accounts, using as fallback).", _accountId);
                    }
                    else
                    {
                        _logger.LogWarning("TradeLocker returned no accounts for the current user. Raw response (first 500 chars): {Body}",
                            Truncate(accountsBody ?? string.Empty, 500));
                    }
                }
            }

            _isConnected = true;

            // Phase 2: Rate-Limits dynamisch aus /trade/config laden (falls verfuegbar)
            try
            {
                await LoadRateLimitsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "TradeLocker: Konnte Rate-Limits aus /trade/config nicht laden – verwende Fallback {DelayMs}ms.",
                    _globalMinDelayMs);
            }

            // Phase 3: Instrumente laden und Start-Log (Kontostand, Equity, Instrumente, Beispiel-Quote)
            if (!string.IsNullOrEmpty(_accountId))
            {
                await LoadInstrumentsAsync(ct);
                try
                {
                    var details = await GetAccountDetailsInternalAsync(ct);
                    if (details != null)
                        _logger.LogDebug("TradeLocker account: Balance={Balance}, Equity={Equity}, Instrumente={Count}",
                            details.Balance, details.Equity, _symbolToInstrumentId.Count);
                    var eurUsdQuote = await GetCurrentPriceAsync("EURUSD", ct);
                    if (eurUsdQuote > 0)
                        _logger.LogDebug("TradeLocker EURUSD Quote: {Quote}", eurUsdQuote);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TradeLocker Phase-3 Start-Log (Konto/Quote) fehlgeschlagen.");
                }
            }
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("TradeLocker login was cancelled.");
            _isConnected = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during TradeLocker login.");
            _isConnected = false;
        }
    }

    public Task DisconnectAsync()
    {
        _isConnected = false;
        _accessToken = null;
        _refreshToken = null;
        _accountNumber = null;
        _balanceFromAllAccounts = 0m;
        _tokenExpiresUtc = DateTime.MinValue;

        _logger.LogInformation("TradeLocker connection reset.");
        return Task.CompletedTask;
    }

    /// <summary>Erneuert den Access-Token via Refresh-Token, wenn er bald abläuft (< 2 Min).</summary>
    private async Task RefreshTokenIfNeededAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_refreshToken)) return;
        if (DateTime.UtcNow < _tokenExpiresUtc.AddMinutes(-2)) return;

        try
        {
            _logger.LogInformation("TradeLocker: Token läuft ab, erneuere via Refresh-Token...");
            var baseUrl = _settings.BaseUrl?.TrimEnd('/') ?? "";
            var refreshUrl = string.IsNullOrEmpty(baseUrl) ? "auth/jwt/refresh" : $"{baseUrl}/auth/jwt/refresh";

            using var client = GetHttpClient();
            var refreshBody = new { refreshToken = _refreshToken };
            var response = await client.PostAsJsonAsync(refreshUrl, refreshBody, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("TradeLocker: Refresh-Token fehlgeschlagen ({Status}). Re-Login erforderlich. Body: {Body}",
                    (int)response.StatusCode, Truncate(errBody, 300));
                _isConnected = false;
                return;
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var auth = JsonSerializer.Deserialize<TradeLockerAuthResponse>(responseBody, JsonOptions);
            if (auth != null && !string.IsNullOrWhiteSpace(auth.AccessToken))
            {
                _accessToken = auth.AccessToken;
                _refreshToken = auth.RefreshToken;
                _tokenExpiresUtc = DateTime.UtcNow.AddMinutes(15);
                _logger.LogInformation("TradeLocker: Token erfolgreich erneuert.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TradeLocker: Refresh-Token Ausnahme. Re-Login wird beim nächsten Zyklus versucht.");
            _isConnected = false;
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static List<TradeLockerAccountInfo>? ParseAccountArray(System.Text.Json.JsonElement arrayElement, System.Text.Json.JsonSerializerOptions options)
    {
        if (arrayElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        var list = new List<TradeLockerAccountInfo>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            try
            {
                var account = System.Text.Json.JsonSerializer.Deserialize<TradeLockerAccountInfo>(item.GetRawText(), options);
                if (account != null && !string.IsNullOrWhiteSpace(account.Id))
                    list.Add(account);
            }
            catch { /* Einzelnes Account-Element ungueltig – naechstes versuchen */ }
        }
        return list.Count > 0 ? list : null;
    }

    // ── Auto-Reconnect (Phase 5.4) ──────────────────────────────────────

    /// <summary>Stellt sicher, dass eine Verbindung besteht. Bei Verlust: exponentielles Backoff (5s, 10s, 30s, 60s).</summary>
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_isConnected) return;

        while (_reconnectAttempt < MaxReconnectAttempts && !ct.IsCancellationRequested)
        {
            _reconnectAttempt++;
            var backoffIndex = Math.Min(_reconnectAttempt - 1, ReconnectBackoffSeconds.Length - 1);
            var delaySec = ReconnectBackoffSeconds[backoffIndex];

            _logger.LogWarning(
                "TradeLocker Verbindung verloren. Reconnect-Versuch {Attempt}/{Max} in {Delay}s...",
                _reconnectAttempt, MaxReconnectAttempts, delaySec);

            await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);

            // Reset für ConnectAsync
            _isConnected = false;
            _accessToken = null;

            try
            {
                await ConnectAsync(ct);
                if (_isConnected)
                {
                    _reconnectAttempt = 0;
                    _logger.LogInformation("TradeLocker Reconnect erfolgreich.");
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TradeLocker Reconnect-Versuch {Attempt} fehlgeschlagen.", _reconnectAttempt);
            }
        }

        if (!_isConnected)
        {
            _logger.LogCritical(
                "TradeLocker Reconnect nach {Max} Versuchen fehlgeschlagen. Naechster Versuch im naechsten Trading-Zyklus.",
                MaxReconnectAttempts);
            _reconnectAttempt = 0; // Reset für nächsten Zyklus
        }
    }

    /// <summary>Markiert Verbindung als verloren bei HTTP-Fehlern die auf Connection-Loss hindeuten.</summary>
    private void HandleConnectionError(HttpStatusCode statusCode, string context)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.BadGateway)
        {
            _logger.LogWarning(
                "TradeLocker Connection-Loss erkannt ({StatusCode}) bei {Context}. Markiere als disconnected.",
                (int)statusCode, context);
            _isConnected = false;
        }
    }

    // ── Rate Limit (aus /trade/config, Fallback ~0,3 Req/s) ───────────────

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await GlobalThrottle.WaitAsync(ct);
        try
        {
            await RefreshTokenIfNeededAsync(ct);

            // Auto-Reconnect wenn disconnected
            if (!_isConnected)
                await EnsureConnectedAsync(ct);

            var now = DateTime.UtcNow;
            var elapsed = (now - _globalLastRequestUtc).TotalMilliseconds;
            var minDelayMs = _globalMinDelayMs;
            if (elapsed < minDelayMs)
            {
                var delayMs = (int)(minDelayMs - elapsed);
                if (delayMs > 0)
                    await Task.Delay(delayMs, ct);
            }
            _globalLastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            GlobalThrottle.Release();
        }
    }

    /// <summary>
    /// Liest die offiziellen Rate-Limits aus /trade/config und passt den globalen Delay
    /// fuer alle TradeLocker-Aufrufe dynamisch an. Fallback bleibt konservativ (3000ms),
    /// wenn die API keine verwertbaren Daten liefert.
    /// </summary>
    private async Task LoadRateLimitsAsync(CancellationToken ct)
    {
        var baseUrl = _settings.BaseUrl?.TrimEnd('/') ?? "";
        var configUrl = string.IsNullOrEmpty(baseUrl) ? "trade/config" : $"{baseUrl}/trade/config";

        using var client = GetHttpClient();
        using var response = await client.GetAsync(configUrl, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("TradeLocker /trade/config nicht erfolgreich ({Status}): {Body}",
                (int)response.StatusCode, Truncate(body, 400));
            return;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json) || json.TrimStart().StartsWith('<'))
        {
            _logger.LogDebug("TradeLocker /trade/config Antwort ist leer oder HTML – ignoriere.");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("rateLimits", out var rateLimitsElement))
            {
                _logger.LogDebug("TradeLocker /trade/config enthaelt kein rateLimits-Objekt – verwende Fallback {DelayMs}ms.",
                    _globalMinDelayMs);
                return;
            }

            JsonElement selectedLimit = default;
            var hasSelected = false;

            // rateLimits kann z.B. ein Objekt mit Routen-Namen sein; bevorzugt "quotes", sonst erstes
            if (rateLimitsElement.ValueKind == JsonValueKind.Object)
            {
                if (rateLimitsElement.TryGetProperty("quotes", out var quotesLimit))
                {
                    selectedLimit = quotesLimit;
                    hasSelected = true;
                }
                else
                {
                    foreach (var prop in rateLimitsElement.EnumerateObject())
                    {
                        selectedLimit = prop.Value;
                        hasSelected = true;
                        break;
                    }
                }
            }
            else if (rateLimitsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rateLimitsElement.EnumerateArray())
                {
                    selectedLimit = item;
                    hasSelected = true;
                    break;
                }
            }

            if (!hasSelected || selectedLimit.ValueKind != JsonValueKind.Object)
            {
                _logger.LogDebug("TradeLocker /trade/config rateLimits nicht verstaendlich – verwende Fallback {DelayMs}ms.",
                    _globalMinDelayMs);
                return;
            }

            if (!selectedLimit.TryGetProperty("limit", out var limitElement) ||
                !selectedLimit.TryGetProperty("intervalNum", out var intervalNumElement))
            {
                _logger.LogDebug("TradeLocker /trade/config rateLimits ohne limit/intervalNum – verwende Fallback {DelayMs}ms.",
                    _globalMinDelayMs);
                return;
            }

            if (!limitElement.TryGetInt32(out var limit) ||
                !intervalNumElement.TryGetInt32(out var intervalNum) ||
                limit <= 0 || intervalNum <= 0)
            {
                _logger.LogDebug("TradeLocker /trade/config rateLimits mit ungueltigen Werten – verwende Fallback {DelayMs}ms.",
                    _globalMinDelayMs);
                return;
            }

            // Einheit bestimmen: default Sekunden, optional "intervalUnit": "SECONDS" | "MINUTES"
            var intervalMs = intervalNum * 1000;
            if (selectedLimit.TryGetProperty("intervalUnit", out var unitElement) &&
                unitElement.ValueKind == JsonValueKind.String)
            {
                var unit = unitElement.GetString();
                if (unit != null && unit.Contains("MINUTE", StringComparison.OrdinalIgnoreCase))
                {
                    intervalMs = intervalNum * 60 * 1000;
                }
            }

            // minDelay = Intervall / limit (z.B. 1 Sekunde / 2 = 500ms)
            var computedDelay = intervalMs / Math.Max(limit, 1);

            // Sicherheitskorridor: nicht < 200ms, nicht > 5000ms
            computedDelay = Math.Clamp(computedDelay, 200, 5000);

            var oldDelay = _globalMinDelayMs;
            _globalMinDelayMs = computedDelay;

            _logger.LogInformation(
                "TradeLocker Rate-Limit konfiguriert: limit={Limit}, interval={IntervalMs}ms → minDelay={DelayMs}ms (alt={OldDelayMs}ms)",
                limit, intervalMs, computedDelay, oldDelay);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TradeLocker: Fehler beim Parsen von /trade/config – verwende Fallback {DelayMs}ms.",
                _globalMinDelayMs);
        }
    }
}
