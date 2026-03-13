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
public class TradeLockerService : IBrokerService
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

    /// <summary>Erzeugt einen pro Request konfigurierten HttpClient (Auth + accNum). Nicht speichern, nach Verwendung verwerfen.</summary>
    private HttpClient GetHttpClient()
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
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
                    var account = accounts?.FirstOrDefault();
                    if (account != null && !string.IsNullOrWhiteSpace(account.Id))
                    {
                        _accountId = account.Id;
                        _accountNumber = !string.IsNullOrWhiteSpace(account.AccNum) ? account.AccNum : "1";
                        _balanceFromAllAccounts = account.AccountBalance;
                        _logger.LogInformation("TradeLocker login successful. Using account {AccountId} (accNum={AccNum}), Balance from all-accounts: {Balance}", account.Id, _accountNumber, _balanceFromAllAccounts);
                    }
                    else
                    {
                        // Fallback: konfiguriertes AccountId nutzen (z. B. aus TradeLocker UI #123456)
                        if (!string.IsNullOrWhiteSpace(_settings.AccountId))
                        {
                            _accountId = _settings.AccountId.Trim();
                            _accountNumber = "1";
                            _balanceFromAllAccounts = 0m;
                            _logger.LogInformation("TradeLocker using configured AccountId {AccountId} (accNum=1). all-accounts returned no list.", _accountId);
                        }
                        else
                        {
                            _logger.LogWarning("TradeLocker returned no accounts for the current user. Raw response (first 500 chars): {Body}",
                                Truncate(accountsBody ?? string.Empty, 500));
                        }
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
                        _logger.LogInformation("TradeLocker account: Balance={Balance}, Equity={Equity}, Instrumente={Count}",
                            details.Balance, details.Equity, _symbolToInstrumentId.Count);
                    var eurUsdQuote = await GetCurrentPriceAsync("EURUSD", ct);
                    if (eurUsdQuote > 0)
                        _logger.LogInformation("TradeLocker EURUSD Quote: {Quote}", eurUsdQuote);
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
            catch { /* einzelnes Element überspringen */ }
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>Parst Instrumente aus verschiedenen TradeLocker-Antwortformaten.</summary>
    private List<TradeLockerInstrumentInfo>? ParseInstruments(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Format 1: Direktes Array [...]
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                return ParseInstrumentArray(root);

            // Format 2: Wrapper { "d": { "instruments": [...] } } oder { "instruments": [...] }
            var target = root;
            if (root.TryGetProperty("d", out var d)) target = d;
            else if (root.TryGetProperty("data", out var data)) target = data;

            if (target.TryGetProperty("instruments", out var instruments))
                return ParseInstrumentArray(instruments);
            if (target.TryGetProperty("symbols", out var symbols))
                return ParseInstrumentArray(symbols);
            if (target.ValueKind == System.Text.Json.JsonValueKind.Array)
                return ParseInstrumentArray(target);

            // Format 3: Versuche direkt als List
            var list = JsonSerializer.Deserialize<List<TradeLockerInstrumentInfo>>(body, JsonOptions);
            if (list != null && list.Count > 0 && list.Any(i => !string.IsNullOrWhiteSpace(i.ResolvedSymbol)))
                return list;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ParseInstruments: Konnte Instrument-Format nicht erkennen.");
        }
        return null;
    }

    private static List<TradeLockerInstrumentInfo>? ParseInstrumentArray(System.Text.Json.JsonElement arrayElement)
    {
        if (arrayElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        var list = new List<TradeLockerInstrumentInfo>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            try
            {
                var inst = JsonSerializer.Deserialize<TradeLockerInstrumentInfo>(item.GetRawText(), JsonOptions);
                if (inst != null && (!string.IsNullOrWhiteSpace(inst.ResolvedSymbol) || inst.TradableInstrumentId != 0 || inst.Id != 0))
                    list.Add(inst);
            }
            catch { /* einzelnes Element überspringen */ }
        }
        return list.Count > 0 ? list : null;
    }

    // ── Rate Limit (~2 Req/s) ─────────────────────────────────────────────

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            await RefreshTokenIfNeededAsync(ct);
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

    // ── Instrumente (einmal beim Start) ───────────────────────────────────

    private async Task LoadInstrumentsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_accountId)) return;
        await ThrottleAsync(ct);
        try
        {
            using var client = GetHttpClient();
            var response = await client.GetAsync($"trade/accounts/{_accountId}/instruments", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TradeLocker instruments failed: {StatusCode}", (int)response.StatusCode);
                return;
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("TradeLocker instruments raw response (300 chars): {Body}", Truncate(body, 300));
            var list = ParseInstruments(body);
            if (list == null || list.Count == 0)
            {
                _logger.LogWarning("TradeLocker: Keine Instrumente geladen. Response (500 chars): {Body}", Truncate(body, 500));
                return;
            }
            lock (_symbolToInstrumentId)
            {
                _symbolToInstrumentId.Clear();
                _instrumentIdToSymbol.Clear();
                _instrumentInfoRouteId.Clear();
                _instrumentTradeRouteId.Clear();
                foreach (var i in list)
                {
                    var sym = i.ResolvedSymbol;
                    if (string.IsNullOrWhiteSpace(sym)) continue;
                    var id = i.TradableInstrumentId != 0 ? i.TradableInstrumentId : i.Id;
                    if (id == 0) continue;
                    _symbolToInstrumentId[sym.Trim()] = id;
                    _instrumentIdToSymbol[id] = sym.Trim();
                    if (i.Routes != null)
                    {
                        var infoRoute = i.Routes.FirstOrDefault(r => r.Type.Equals("INFO", StringComparison.OrdinalIgnoreCase));
                        var tradeRoute = i.Routes.FirstOrDefault(r => r.Type.Equals("TRADE", StringComparison.OrdinalIgnoreCase));
                        if (infoRoute != null) _instrumentInfoRouteId[id] = infoRoute.Id;
                        if (tradeRoute != null) _instrumentTradeRouteId[id] = tradeRoute.Id;
                    }
                }
            }
            _logger.LogInformation("TradeLocker loaded {Count} instruments. Beispiele: {Samples}",
                _symbolToInstrumentId.Count,
                string.Join(", ", _symbolToInstrumentId.Take(10).Select(kv => $"{kv.Key}={kv.Value}")));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TradeLocker LoadInstruments failed.");
        }
    }

    private int? ResolveSymbolToInstrumentId(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        lock (_symbolToInstrumentId)
        {
            return _symbolToInstrumentId.TryGetValue(symbol.Trim(), out var id) ? id : null;
        }
    }

    private string? ResolveInstrumentIdToSymbol(int instrumentId)
    {
        lock (_instrumentIdToSymbol)
        {
            return _instrumentIdToSymbol.TryGetValue(instrumentId, out var s) ? s : null;
        }
    }

    private int? GetInfoRouteId(int instrumentId)
    {
        lock (_instrumentInfoRouteId)
        {
            return _instrumentInfoRouteId.TryGetValue(instrumentId, out var id) ? id : null;
        }
    }

    private int? GetTradeRouteId(int instrumentId)
    {
        lock (_instrumentTradeRouteId)
        {
            return _instrumentTradeRouteId.TryGetValue(instrumentId, out var id) ? id : null;
        }
    }

    // ── Konto-Details ────────────────────────────────────────────────────

    /// <summary>Konto-Details (Balance, Equity). Viele Broker/Umgebungen liefern 404 – dann wird die Balance aus all-accounts verwendet.</summary>
    private async Task<TradeLockerAccountDetails?> GetAccountDetailsInternalAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_accountId)) return null;
        await ThrottleAsync(ct);
        try
        {
            using var client = GetHttpClient();
            var response = await client.GetAsync($"trade/accounts/{_accountId}/details", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    _logger.LogInformation("TradeLocker: Endpunkt account/details nicht verfügbar (404). Nutze Balance aus all-accounts.");
                else
                    _logger.LogWarning("TradeLocker account details failed: Status {StatusCode}, Response: {Body}",
                        (int)response.StatusCode, Truncate(body, 500));
                return null;
            }
            // Wrapper-Format erkennen: { "data": { ... } } oder { "accountDetails": { ... } }
            TradeLockerAccountDetails? details = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                var target = root;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Object)
                    target = data;
                else if (root.TryGetProperty("accountDetails", out var ad) && ad.ValueKind == System.Text.Json.JsonValueKind.Object)
                    target = ad;
                details = JsonSerializer.Deserialize<TradeLockerAccountDetails>(target.GetRawText(), JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TradeLocker account details: Wrapper-Parse fehlgeschlagen, versuche direkte Deserialisierung.");
                details = JsonSerializer.Deserialize<TradeLockerAccountDetails>(body, JsonOptions);
            }
            if (details == null)
            {
                _logger.LogWarning("TradeLocker account details: Deserialize returned null. Response: {Body}", Truncate(body, 500));
                return null;
            }
            var bal = details.Balance != 0 ? details.Balance : details.AccountBalance;
            var eq = details.Equity != 0 ? details.Equity : details.AccountEquity;
            if (bal == 0 && eq == 0)
            {
                _logger.LogWarning("TradeLocker account details: Balance/Equity sind 0. Rohantwort: {Body}",
                    Truncate(body, 800));
            }
            return details;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TradeLocker GetAccountDetailsInternalAsync exception.");
            return null;
        }
    }

    // ── History (Candles) ────────────────────────────────────────────────

    /// <summary>Historische Kurse für ein Instrument (z. B. 1D, 4H, 1H).</summary>
    private async Task<List<decimal>> GetPriceHistoryInternalAsync(int tradableInstrumentId, string resolution, int lookbackCandles, CancellationToken ct)
    {
        await ThrottleAsync(ct);
        var result = new List<decimal>();
        try
        {
            var to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var resolutionMs = resolution switch
            {
                "1D" => 24 * 60 * 60 * 1000L,
                "4H" => 4 * 60 * 60 * 1000L,
                "1H" => 60 * 60 * 1000L,
                "15" or "15m" => 15 * 60 * 1000L,
                _ => 60 * 60 * 1000L
            };
            var from = to - (lookbackCandles * resolutionMs);
            var routeId = GetInfoRouteId(tradableInstrumentId);
            if (routeId == null)
            {
                _logger.LogDebug("GetPriceHistory: Keine INFO-Route für Instrument {Id}", tradableInstrumentId);
                return result;
            }
            var url = $"trade/history?tradableInstrumentId={tradableInstrumentId}&routeId={routeId}&resolution={resolution}&from={from}&to={to}";
            using var client = GetHttpClient();
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogDebug("GetPriceHistory failed for instrument {Id} ({Res}): {Status} – {Body}",
                    tradableInstrumentId, resolution, (int)response.StatusCode, Truncate(errBody, 200));
                return result;
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            var candles = ParseCandles(body);
            if (candles != null && candles.Count > 0)
            {
                result.AddRange(candles.OrderBy(c => c.Time).Select(c => c.Close).TakeLast(lookbackCandles));
                _logger.LogDebug("GetPriceHistory: {Count} candles for instrument {Id} ({Res})", result.Count, tradableInstrumentId, resolution);
            }
            else
            {
                _logger.LogWarning("GetPriceHistory: Keine Candles für Instrument {Id} ({Res}). Response (200 chars): {Body}",
                    tradableInstrumentId, resolution, Truncate(body, 200));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetPriceHistory failed for instrument {Id} ({Res})", tradableInstrumentId, resolution);
        }
        return result;
    }

    /// <summary>Parst Candles aus verschiedenen TradeLocker-Antwortformaten.</summary>
    private List<TradeLockerCandle>? ParseCandles(string body)
    {
        // Format 1: Direktes Array [ { open, high, low, close, time }, ... ]
        try
        {
            var list = JsonSerializer.Deserialize<List<TradeLockerCandle>>(body, JsonOptions);
            if (list != null && list.Count > 0 && list.Any(c => c.Close != 0))
                return list;
        }
        catch (System.Text.Json.JsonException) { }

        // Format 2: Wrapper-Objekte { "d": { ... }, "candles": [...] } etc.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;

            // TradeLocker liefert oft { "d": { "candles": [[t,o,h,l,c], ...] } }
            System.Text.Json.JsonElement target = root;
            if (root.TryGetProperty("d", out var d)) target = d;
            else if (root.TryGetProperty("data", out var data)) target = data;

            // Suche nach candles/bars Array
            System.Text.Json.JsonElement? candleArray = null;
            if (target.TryGetProperty("barDetails", out var bd)) candleArray = bd;
            else if (target.TryGetProperty("candles", out var c)) candleArray = c;
            else if (target.TryGetProperty("bars", out var b)) candleArray = b;
            else if (target.TryGetProperty("history", out var h)) candleArray = h;
            else if (target.ValueKind == System.Text.Json.JsonValueKind.Array) candleArray = target;

            if (candleArray?.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var candles = new List<TradeLockerCandle>();
                foreach (var item in candleArray.Value.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        // { "o": 1.08, "h": 1.09, ... } oder { "open": 1.08, ... }
                        var candle = JsonSerializer.Deserialize<TradeLockerCandle>(item.GetRawText(), JsonOptions);
                        if (candle != null) candles.Add(candle);
                    }
                    else if (item.ValueKind == System.Text.Json.JsonValueKind.Array && item.GetArrayLength() >= 5)
                    {
                        // TradeLocker Array-Format: [time, open, high, low, close] oder [time, open, high, low, close, volume]
                        candles.Add(new TradeLockerCandle
                        {
                            Time = item[0].GetInt64(),
                            Open = item[1].GetDecimal(),
                            High = item[2].GetDecimal(),
                            Low = item[3].GetDecimal(),
                            Close = item[4].GetDecimal()
                        });
                    }
                }
                if (candles.Count > 0) return candles;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ParseCandles: Konnte Candle-Format nicht erkennen.");
        }

        return null;
    }

    // ── IBrokerService-Implementierung ────────────────────────────────────

    public async Task<decimal> GetCurrentPriceAsync(string symbol, CancellationToken ct = default)
    {
        var (bid, ask) = await GetBidAskAsync(symbol, ct);
        if (bid != 0 || ask != 0)
            return (bid + ask) / 2;
        return 0m;
    }

    public async Task<(decimal Bid, decimal Ask)> GetBidAskAsync(string symbol, CancellationToken ct = default)
    {
        if (!_isConnected) return (0m, 0m);
        var id = ResolveSymbolToInstrumentId(symbol);
        if (id == null) return (0m, 0m);
        await ThrottleAsync(ct);
        try
        {
            using var client = GetHttpClient();
            var routeId = GetInfoRouteId(id.Value);
            if (routeId == null)
            {
                _logger.LogDebug("GetBidAsk: Keine INFO-Route für {Symbol} (instrumentId={Id})", symbol, id);
                return (0m, 0m);
            }
            var response = await client.GetAsync($"trade/quotes?tradableInstrumentId={id}&routeId={routeId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogDebug("GetBidAsk failed for {Symbol}: {Status} – {Body}", symbol, (int)response.StatusCode, Truncate(errBody, 200));
                return (0m, 0m);
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("GetBidAsk raw response for {Symbol} (200 chars): {Body}", symbol, Truncate(body, 200));
            var (bid, ask) = ParseQuote(body, id.Value);
            if (bid == 0 && ask == 0)
                _logger.LogWarning("GetBidAsk: Bid/Ask sind 0 für {Symbol}. Response (200 chars): {Body}", symbol, Truncate(body, 200));
            return (bid, ask);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetBidAsk failed for {Symbol}", symbol);
            return (0m, 0m);
        }
    }

    /// <summary>Parst Bid/Ask aus verschiedenen TradeLocker-Antwortformaten.</summary>
    private (decimal Bid, decimal Ask) ParseQuote(string body, int instrumentId)
    {
        // Format 1: Direktes Objekt { "bid": 1.08, "ask": 1.09, ... }
        try
        {
            var quote = JsonSerializer.Deserialize<TradeLockerQuote>(body, JsonOptions);
            if (quote != null && (quote.Bid != 0 || quote.Ask != 0))
                return (quote.Bid, quote.Ask);
        }
        catch (System.Text.Json.JsonException) { }

        // Format 2: Array [ { "bid": ..., "ask": ... } ]
        try
        {
            var list = JsonSerializer.Deserialize<List<TradeLockerQuote>>(body, JsonOptions);
            var q = list?.FirstOrDefault(x => x.TradableInstrumentId == instrumentId)
                 ?? list?.FirstOrDefault();
            if (q != null && (q.Bid != 0 || q.Ask != 0))
                return (q.Bid, q.Ask);
        }
        catch (System.Text.Json.JsonException) { }

        // Format 3: Wrapper { "d": { "ask": [1.09], "bid": [1.08] } } oder { "d": { "ask": 1.09, "bid": 1.08 } }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            var target = root;
            if (root.TryGetProperty("d", out var d)) target = d;
            else if (root.TryGetProperty("data", out var data)) target = data;

            decimal bid = 0, ask = 0;
            // TradeLocker Format: bp=bid price, ap=ask price
            if (target.TryGetProperty("bp", out var bpEl) && bpEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                bid = bpEl.GetDecimal();
            if (target.TryGetProperty("ap", out var apEl) && apEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                ask = apEl.GetDecimal();
            if (bid != 0 || ask != 0) return (bid, ask);
            // Fallback: bid/ask als Feld oder Array
            if (target.TryGetProperty("bid", out var bidEl))
            {
                if (bidEl.ValueKind == System.Text.Json.JsonValueKind.Array && bidEl.GetArrayLength() > 0)
                    bid = bidEl[0].GetDecimal();
                else if (bidEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    bid = bidEl.GetDecimal();
            }
            if (target.TryGetProperty("ask", out var askEl))
            {
                if (askEl.ValueKind == System.Text.Json.JsonValueKind.Array && askEl.GetArrayLength() > 0)
                    ask = askEl[0].GetDecimal();
                else if (askEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    ask = askEl.GetDecimal();
            }
            if (bid != 0 || ask != 0) return (bid, ask);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ParseQuote: Konnte Quote-Format nicht erkennen.");
        }

        return (0m, 0m);
    }

    public async Task<List<decimal>> GetRecentPricesAsync(string symbol, int count = 20, CancellationToken ct = default)
    {
        return await GetPriceHistoryAsync(symbol, "1H", count, ct);
    }

    public async Task<List<decimal>> GetPriceHistoryAsync(string symbol, string resolution, int count, CancellationToken ct = default)
    {
        var candles = await GetCandlesAsync(symbol, resolution, count, ct);
        if (candles.Count > 0)
            return candles.Select(c => c.Close).ToList();

        // Fallback auf alte Methode
        if (!_isConnected) return new List<decimal>();
        var id = ResolveSymbolToInstrumentId(symbol);
        if (id == null) return new List<decimal>();
        var prices = await GetPriceHistoryInternalAsync(id.Value, resolution, Math.Max(count, 50), ct);
        return prices.TakeLast(count).ToList();
    }

    public async Task<List<OhlcCandle>> GetCandlesAsync(string symbol, string resolution, int count, CancellationToken ct = default)
    {
        if (!_isConnected) return new List<OhlcCandle>();
        var id = ResolveSymbolToInstrumentId(symbol);
        if (id == null) return new List<OhlcCandle>();

        var candles = await GetCandlesInternalAsync(id.Value, resolution, Math.Max(count, 50), ct);
        return candles.TakeLast(count).ToList();
    }

    private async Task<List<OhlcCandle>> GetCandlesInternalAsync(int tradableInstrumentId, string resolution, int lookbackCandles, CancellationToken ct)
    {
        await ThrottleAsync(ct);
        var result = new List<OhlcCandle>();
        try
        {
            var to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var resolutionMs = resolution switch
            {
                "1D" => 24 * 60 * 60 * 1000L,
                "4H" => 4 * 60 * 60 * 1000L,
                "1H" => 60 * 60 * 1000L,
                "15" or "15m" => 15 * 60 * 1000L,
                _ => 60 * 60 * 1000L
            };
            var from = to - (lookbackCandles * resolutionMs);
            var routeId = GetInfoRouteId(tradableInstrumentId);
            if (routeId == null) return result;

            var url = $"trade/history?tradableInstrumentId={tradableInstrumentId}&routeId={routeId}&resolution={resolution}&from={from}&to={to}";
            using var client = GetHttpClient();
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return result;

            var body = await response.Content.ReadAsStringAsync(ct);
            var parsed = ParseCandles(body);
            if (parsed != null && parsed.Count > 0)
            {
                result.AddRange(parsed.OrderBy(c => c.Time).TakeLast(lookbackCandles).Select(c => new OhlcCandle
                {
                    Open = c.Open,
                    High = c.High,
                    Low = c.Low,
                    Close = c.Close,
                    Time = c.Time
                }));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetCandlesInternal failed for instrument {Id} ({Res})", tradableInstrumentId, resolution);
        }
        return result;
    }

    public async Task<decimal> GetAccountCashAsync(CancellationToken ct = default)
    {
        if (_balanceFromAllAccounts > 0)
            return _balanceFromAllAccounts;
        var details = await GetAccountDetailsInternalAsync(ct);
        if (details != null)
        {
            var cash = details.Balance != 0 ? details.Balance : details.AccountBalance;
            if (cash != 0) return cash;
        }
        return _balanceFromAllAccounts;
    }

    public async Task<decimal> GetPortfolioValueAsync(CancellationToken ct = default)
    {
        if (_balanceFromAllAccounts > 0)
            return _balanceFromAllAccounts;
        var details = await GetAccountDetailsInternalAsync(ct);
        if (details != null)
        {
            var equity = details.Equity != 0 ? details.Equity : details.AccountEquity;
            if (equity != 0) return equity;
        }
        return _balanceFromAllAccounts;
    }

    /// <summary>Parst Positionen aus TradeLockers Array-of-Arrays-Format:
    /// {"d":{"positions":[["id","instrumentId","routeId","side","qty","avgPrice",...]]}}
    /// Indices: [0]=id, [1]=instrumentId, [2]=routeId, [3]=side, [4]=qty, [5]=avgPrice</summary>
    private List<TradeLockerPositionInfo>? ParsePositions(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;

            System.Text.Json.JsonElement target = root;
            if (root.TryGetProperty("d", out var d)) target = d;
            else if (root.TryGetProperty("data", out var data)) target = data;

            if (!target.TryGetProperty("positions", out var positionsEl) ||
                positionsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                _logger.LogDebug("ParsePositions: Kein 'positions'-Array gefunden. Body: {Body}", Truncate(body, 300));
                return null;
            }

            var result = new List<TradeLockerPositionInfo>();
            foreach (var item in positionsEl.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.Array && item.GetArrayLength() >= 6)
                {
                    // Array-Format: [id, instrumentId, routeId, side, qty, avgPrice, ...]
                    var pos = new TradeLockerPositionInfo
                    {
                        Id = item[0].ToString(),
                        TradableInstrumentId = int.Parse(item[1].ToString()),
                        Side = item[3].ToString(),
                        Qty = decimal.Parse(item[4].ToString(), System.Globalization.CultureInfo.InvariantCulture),
                        AvgPrice = decimal.Parse(item[5].ToString(), System.Globalization.CultureInfo.InvariantCulture)
                    };
                    result.Add(pos);
                    _logger.LogDebug("ParsePositions: Position {Id} instId={InstId} side={Side} qty={Qty} avgPrice={AvgPrice}",
                        pos.Id, pos.TradableInstrumentId, pos.Side, pos.Qty, pos.AvgPrice);
                }
                else if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    // Fallback: Objekt-Format
                    var pos = JsonSerializer.Deserialize<TradeLockerPositionInfo>(item.GetRawText(), JsonOptions);
                    if (pos != null) result.Add(pos);
                }
            }

            _logger.LogInformation("ParsePositions: {Count} Positionen geparst", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ParsePositions: Konnte Position-Format nicht erkennen. Body: {Body}", Truncate(body, 300));
            return null;
        }
    }

    public async Task<List<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        var list = new List<Position>();
        if (!_isConnected || string.IsNullOrEmpty(_accountId)) return list;
        await ThrottleAsync(ct);
        try
        {
            using var client = GetHttpClient();
            var response = await client.GetAsync($"trade/accounts/{_accountId}/positions", ct);
            if (!response.IsSuccessStatusCode) return list;
            var body = await response.Content.ReadAsStringAsync(ct);
            var parsedPositions = ParsePositions(body);
            if (parsedPositions == null || parsedPositions.Count == 0) return list;
            foreach (var p in parsedPositions)
            {
                var symbol = ResolveInstrumentIdToSymbol(p.TradableInstrumentId);
                if (symbol == null)
                {
                    _logger.LogWarning("TradeLocker: Konnte InstrumentId {Id} nicht zu Symbol auflösen – Position wird übersprungen.", p.TradableInstrumentId);
                    continue;
                }
                list.Add(new Position
                {
                    Symbol = symbol,
                    Side = p.Side,
                    Quantity = p.Qty,
                    BrokerPositionId = p.Id,
                    AveragePrice = p.AvgPrice,
                    CurrentPrice = p.MarketPrice,
                    LastUpdated = DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetPositions failed.");
        }
        return list;
    }

    public async Task<PlaceOrderResult> PlaceOrderAsync(string symbol, TradeAction action, decimal quantityLots, decimal? stopLoss, decimal? takeProfit, CancellationToken ct = default)
    {
        if (!_isConnected || string.IsNullOrEmpty(_accountId))
        {
            _logger.LogWarning("TradeLocker: Cannot place order – not connected or no account.");
            return new PlaceOrderResult { Success = false };
        }
        var tradableInstrumentId = ResolveSymbolToInstrumentId(symbol);
        if (tradableInstrumentId == null)
        {
            _logger.LogWarning("TradeLocker: Unknown symbol {Symbol}", symbol);
            return new PlaceOrderResult { Success = false };
        }
        if (quantityLots <= 0)
        {
            _logger.LogWarning("TradeLocker: Invalid quantity {Qty}", quantityLots);
            return new PlaceOrderResult { Success = false };
        }
        await ThrottleAsync(ct);
        try
        {
            var tradeRouteId = GetTradeRouteId(tradableInstrumentId.Value);
            if (tradeRouteId == null)
            {
                _logger.LogWarning("TradeLocker: Keine TRADE-Route für {Symbol}", symbol);
                return new PlaceOrderResult { Success = false };
            }
            var side = action == TradeAction.Buy ? "buy" : "sell";
            var body = new TradeLockerOrderRequest
            {
                Price = 0m,
                Qty = quantityLots,
                RouteId = tradeRouteId.Value,
                Side = side,
                StopLoss = stopLoss,
                StopLossType = stopLoss.HasValue ? "absolute" : null,
                TakeProfit = takeProfit,
                TakeProfitType = takeProfit.HasValue ? "absolute" : null,
                TrStopOffset = 0m,
                TradableInstrumentId = tradableInstrumentId.Value,
                Type = "market",
                Validity = "IOC"
            };
            using var client = GetHttpClient();
            var requestJson = JsonSerializer.Serialize(body, JsonOptions);
            _logger.LogDebug("TradeLocker PlaceOrder request: {Json}", requestJson);
            var response = await client.PostAsJsonAsync($"trade/accounts/{_accountId}/orders", body, JsonOptions, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("TradeLocker PlaceOrder response: {Status} – {Body}", (int)response.StatusCode, Truncate(responseBody, 400));
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TradeLocker PlaceOrder failed: {StatusCode} – {Body}", (int)response.StatusCode, Truncate(responseBody, 400));
                return new PlaceOrderResult { Success = false };
            }
            var orderResponse = JsonSerializer.Deserialize<TradeLockerOrderResponse>(responseBody, JsonOptions);
            var orderId = orderResponse?.Id;
            _logger.LogInformation("TradeLocker order placed: {Symbol} {Side} {Qty} Lots, orderId={OrderId}, response={Body}",
                symbol, side, quantityLots, orderId, Truncate(responseBody, 200));
            // Market-Orders werden sofort gefüllt – Position-ID ermitteln
            string? positionId = null;
            try
            {
                await Task.Delay(500, ct);
                var positions = await GetPositionsAsync(ct);
                var newPos = positions.FirstOrDefault(p =>
                    p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
                positionId = newPos?.BrokerPositionId;
                if (positionId != null)
                    _logger.LogDebug("TradeLocker: Position-ID nach Order ermittelt: {PositionId}", positionId);
            }
            catch (Exception posEx)
            {
                _logger.LogDebug(posEx, "TradeLocker: Konnte Position-ID nach Order nicht ermitteln");
            }

            return new PlaceOrderResult
            {
                Success = true,
                BrokerOrderId = orderId,
                BrokerPositionId = positionId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TradeLocker PlaceOrder exception for {Symbol}", symbol);
            return new PlaceOrderResult { Success = false };
        }
    }

    public async Task<bool> ClosePositionAsync(string positionIdOrSymbol, decimal? quantity, CancellationToken ct = default)
    {
        if (!_isConnected || string.IsNullOrEmpty(_accountId))
        {
            _logger.LogWarning("TradeLocker: Cannot close position – not connected or no account.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(positionIdOrSymbol))
        {
            _logger.LogWarning("TradeLocker: ClosePosition called with empty positionIdOrSymbol.");
            return false;
        }
        await ThrottleAsync(ct);
        try
        {
            string positionId;
            if (int.TryParse(positionIdOrSymbol, out _) || positionIdOrSymbol.Length > 10)
            {
                positionId = positionIdOrSymbol;
            }
            else
            {
                var positions = await GetPositionsAsync(ct);
                var pos = positions.FirstOrDefault(p => p.Symbol.Equals(positionIdOrSymbol, StringComparison.OrdinalIgnoreCase));
                if (pos == null)
                {
                    _logger.LogWarning("TradeLocker: No position found for symbol {Symbol}", positionIdOrSymbol);
                    return false;
                }
                positionId = pos.BrokerPositionId ?? positionIdOrSymbol;
            }
            using var client = GetHttpClient();
            var url = quantity.HasValue && quantity.Value > 0
                ? $"trade/positions/{positionId}?qty={quantity.Value}"
                : $"trade/positions/{positionId}";
            var request = new HttpRequestMessage(HttpMethod.Delete, url);
            var response = await client.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TradeLocker ClosePosition failed: {StatusCode} – {Body}", (int)response.StatusCode, Truncate(responseBody, 400));
                return false;
            }
            _logger.LogInformation("TradeLocker position closed: positionId={PositionId}, qty={Qty}", positionId, quantity);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TradeLocker ClosePosition exception for {Id}", positionIdOrSymbol);
            return false;
        }
    }

    public async Task<bool> UpdatePositionStopLossAsync(string positionId, decimal newStopLoss, CancellationToken ct = default)
    {
        if (!_isConnected || string.IsNullOrEmpty(_accountId)) return false;
        await ThrottleAsync(ct);
        try
        {
            using var client = GetHttpClient();
            var body = new { stopLoss = newStopLoss, stopLossType = "absolute" };
            var response = await client.PatchAsJsonAsync($"trade/positions/{positionId}", body, JsonOptions, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("UpdatePositionSL failed for {PosId}: {Status} – {Body}",
                    positionId, (int)response.StatusCode, Truncate(responseBody, 200));
                return false;
            }
            _logger.LogInformation("TradeLocker SL updated: positionId={PosId}, newSL={SL:F5}", positionId, newStopLoss);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UpdatePositionSL exception for {PosId}", positionId);
            return false;
        }
    }

    public async Task<List<BrokerClosedPosition>> GetClosedPositionsAsync(int lookbackDays = 1, CancellationToken ct = default)
    {
        var list = new List<BrokerClosedPosition>();
        if (!_isConnected || string.IsNullOrEmpty(_accountId)) return list;
        await ThrottleAsync(ct);
        try
        {
            using var client = GetHttpClient();
            var response = await client.GetAsync($"trade/accounts/{_accountId}/positionsHistory", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("GetClosedPositions failed: {Status}", (int)response.StatusCode);
                return list;
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("GetClosedPositions raw (300 chars): {Body}", Truncate(body, 300));

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            System.Text.Json.JsonElement target = root;
            if (root.TryGetProperty("d", out var d)) target = d;

            if (!target.TryGetProperty("positionsHistory", out var histEl) ||
                histEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                // Alternativer Key
                if (!target.TryGetProperty("positions", out histEl) ||
                    histEl.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return list;
            }

            var cutoff = DateTime.UtcNow.AddDays(-lookbackDays);

            foreach (var item in histEl.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Array || item.GetArrayLength() < 6)
                    continue;

                // Array-Format: [id, instrumentId, routeId, side, qty, avgOpenPrice, avgClosePrice, pnl, closeTimestamp, ...]
                var posId = item[0].ToString();
                var instId = int.TryParse(item[1].ToString(), out var iid) ? iid : 0;
                var side = item[3].ToString();
                var qty = decimal.TryParse(item[4].ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var q) ? q : 0m;
                var openPrice = decimal.TryParse(item[5].ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var op) ? op : 0m;

                // Optionale Felder ab Index 6
                var closePrice = item.GetArrayLength() > 6
                    ? (decimal.TryParse(item[6].ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var cp) ? cp : 0m)
                    : 0m;
                var pnl = item.GetArrayLength() > 7
                    ? (decimal.TryParse(item[7].ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pl) ? pl : 0m)
                    : 0m;

                DateTime closedAt = DateTime.UtcNow;
                if (item.GetArrayLength() > 8)
                {
                    var tsStr = item[8].ToString();
                    if (long.TryParse(tsStr, out var tsMs))
                        closedAt = DateTimeOffset.FromUnixTimeMilliseconds(tsMs).UtcDateTime;
                }

                if (closedAt < cutoff) continue;

                var symbol = ResolveInstrumentIdToSymbol(instId) ?? $"UNKNOWN_{instId}";

                list.Add(new BrokerClosedPosition
                {
                    PositionId = posId,
                    Symbol = symbol,
                    Side = side,
                    Qty = qty,
                    OpenPrice = openPrice,
                    ClosePrice = closePrice,
                    PnL = pnl,
                    ClosedAt = closedAt
                });
            }

            _logger.LogInformation("GetClosedPositions: {Count} geschlossene Positionen in den letzten {Days} Tagen", list.Count, lookbackDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetClosedPositions failed");
        }
        return list;
    }
}

