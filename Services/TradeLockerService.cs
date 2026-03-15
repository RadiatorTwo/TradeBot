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
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;
    private const int MinDelayMs = 500; // ~2 Req/s

    // ── Caching ────────────────────────────────────────────────────────────
    private AccountDetails? _cachedAccountDetails;
    private List<Position>? _cachedPositions;
    private DateTime _accountDetailsCacheExpiry = DateTime.MinValue;
    private DateTime _positionsCacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

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

            // Phase 2: Instrumente laden und Start-Log (Kontostand, Equity, Instrumente, Beispiel-Quote)
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
                    _logger.LogWarning(ex, "TradeLocker Phase-2 Start-Log (Konto/Quote) fehlgeschlagen.");
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

    // ── Rate Limit (~2 Req/s) ─────────────────────────────────────────────

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            await RefreshTokenIfNeededAsync(ct);

            // Auto-Reconnect wenn disconnected
            if (!_isConnected)
                await EnsureConnectedAsync(ct);

            var elapsed = (DateTime.UtcNow - _lastRequestUtc).TotalMilliseconds;
            if (elapsed < MinDelayMs)
                await Task.Delay((int)(MinDelayMs - elapsed), ct);
            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _throttle.Release();
        }
    }
}
