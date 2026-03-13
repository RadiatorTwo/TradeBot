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
        PropertyNameCaseInsensitive = true
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

    /// <summary>Symbol (z. B. EURUSD) → tradableInstrumentId</summary>
    private readonly Dictionary<string, int> _symbolToInstrumentId = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>tradableInstrumentId → Symbol (für Position-Mapping)</summary>
    private readonly Dictionary<int, string> _instrumentIdToSymbol = new();
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

        _logger.LogInformation("TradeLocker connection reset.");
        return Task.CompletedTask;
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

    private static List<TradeLockerInstrumentInfo>? ParseInstrumentArray(System.Text.Json.JsonElement arrayElement)
    {
        if (arrayElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        var list = new List<TradeLockerInstrumentInfo>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            try
            {
                var inst = JsonSerializer.Deserialize<TradeLockerInstrumentInfo>(item.GetRawText(), JsonOptions);
                if (inst != null && (!string.IsNullOrWhiteSpace(inst.Symbol) || inst.TradableInstrumentId != 0 || inst.Id != 0))
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
            List<TradeLockerInstrumentInfo>? list = null;
            try
            {
                list = JsonSerializer.Deserialize<List<TradeLockerInstrumentInfo>>(body, JsonOptions);
            }
            catch (System.Text.Json.JsonException)
            {
                // API liefert oft Objekt mit Array, z. B. { "instruments": [...] }
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                        list = JsonSerializer.Deserialize<List<TradeLockerInstrumentInfo>>(body, JsonOptions);
                    else if (root.TryGetProperty("instruments", out var arr))
                        list = ParseInstrumentArray(arr);
                    else if (root.TryGetProperty("data", out var data))
                        list = ParseInstrumentArray(data);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogDebug(ex, "TradeLocker instruments response: {Body}", Truncate(body, 300));
                    throw;
                }
            }
            if (list == null || list.Count == 0) return;
            lock (_symbolToInstrumentId)
            {
                _symbolToInstrumentId.Clear();
                _instrumentIdToSymbol.Clear();
                foreach (var i in list)
                {
                    if (string.IsNullOrWhiteSpace(i.Symbol)) continue;
                    var id = i.TradableInstrumentId != 0 ? i.TradableInstrumentId : i.Id;
                    if (id == 0) continue;
                    _symbolToInstrumentId[i.Symbol.Trim()] = id;
                    _instrumentIdToSymbol[id] = i.Symbol.Trim();
                }
            }
            _logger.LogInformation("TradeLocker loaded {Count} instruments", _symbolToInstrumentId.Count);
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
            var details = JsonSerializer.Deserialize<TradeLockerAccountDetails>(body, JsonOptions);
            if (details == null)
            {
                _logger.LogWarning("TradeLocker account details: Deserialize returned null. Response: {Body}", Truncate(body, 500));
                return null;
            }
            // Wrapper-Format: { "data": { "balance": ... } } oder { "accountDetails": { ... } }
            if ((details.Balance == 0 && details.AccountBalance == 0) && (details.Equity == 0 && details.AccountEquity == 0))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    var target = root;
                    if (root.TryGetProperty("data", out var data)) target = data;
                    else if (root.TryGetProperty("accountDetails", out var ad)) target = ad;
                    if (target.ValueKind == System.Text.Json.JsonValueKind.Object)
                        details = JsonSerializer.Deserialize<TradeLockerAccountDetails>(target.GetRawText(), JsonOptions) ?? details;
                }
                catch { /* Wrapper-Parse fehlgeschlagen */ }
                var bal = details.Balance != 0 ? details.Balance : details.AccountBalance;
                var eq = details.Equity != 0 ? details.Equity : details.AccountEquity;
                if (bal == 0 && eq == 0)
                {
                    _logger.LogWarning("TradeLocker account details: Balance/Equity sind 0. Rohantwort (damit wir das API-Format sehen): {Body}",
                        Truncate(body, 800));
                }
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
            // Typische Parameter: from/to als Unix-ms oder count
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
            var url = $"trade/history?tradableInstrumentId={tradableInstrumentId}&resolution={resolution}&from={from}&to={to}";
            using var client = GetHttpClient();
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return result;
            var body = await response.Content.ReadAsStringAsync(ct);
            var candles = JsonSerializer.Deserialize<List<TradeLockerCandle>>(body, JsonOptions);
            if (candles != null)
                result.AddRange(candles.OrderBy(c => c.Time).Select(c => c.Close).TakeLast(lookbackCandles));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetPriceHistory failed for instrument {Id}", tradableInstrumentId);
        }
        return result;
    }

    // ── IBrokerService-Implementierung ────────────────────────────────────

    public async Task<decimal> GetCurrentPriceAsync(string symbol, CancellationToken ct = default)
    {
        if (!_isConnected) return 0m;
        var id = ResolveSymbolToInstrumentId(symbol);
        if (id == null) return 0m;
        await ThrottleAsync(ct);
        try
        {
            using var client = GetHttpClient();
            var response = await client.GetAsync($"trade/quotes?tradableInstrumentId={id}", ct);
            if (!response.IsSuccessStatusCode) return 0m;
            var body = await response.Content.ReadAsStringAsync(ct);
            // API kann einzelnes Objekt oder Array liefern
            var quote = JsonSerializer.Deserialize<TradeLockerQuote>(body, JsonOptions);
            if (quote != null && (quote.Bid != 0 || quote.Ask != 0))
                return (quote.Bid + quote.Ask) / 2;
            var list = JsonSerializer.Deserialize<List<TradeLockerQuote>>(body, JsonOptions);
            var q = list?.FirstOrDefault(x => x.TradableInstrumentId == id);
            return q != null ? (q.Bid + q.Ask) / 2 : 0m;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetCurrentPrice failed for {Symbol}", symbol);
            return 0m;
        }
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
            var response = await client.GetAsync($"trade/quotes?tradableInstrumentId={id}", ct);
            if (!response.IsSuccessStatusCode) return (0m, 0m);
            var body = await response.Content.ReadAsStringAsync(ct);
            var quote = JsonSerializer.Deserialize<TradeLockerQuote>(body, JsonOptions);
            if (quote != null && (quote.Bid != 0 || quote.Ask != 0))
                return (quote.Bid, quote.Ask);
            var list = JsonSerializer.Deserialize<List<TradeLockerQuote>>(body, JsonOptions);
            var q = list?.FirstOrDefault(x => x.TradableInstrumentId == id);
            return q != null ? (q.Bid, q.Ask) : (0m, 0m);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetBidAsk failed for {Symbol}", symbol);
            return (0m, 0m);
        }
    }

    public async Task<List<decimal>> GetRecentPricesAsync(string symbol, int count = 20, CancellationToken ct = default)
    {
        return await GetPriceHistoryAsync(symbol, "1H", count, ct);
    }

    public async Task<List<decimal>> GetPriceHistoryAsync(string symbol, string resolution, int count, CancellationToken ct = default)
    {
        if (!_isConnected) return new List<decimal>();
        var id = ResolveSymbolToInstrumentId(symbol);
        if (id == null) return new List<decimal>();
        var prices = await GetPriceHistoryInternalAsync(id.Value, resolution, Math.Max(count, 50), ct);
        return prices.TakeLast(count).ToList();
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
            var positions = JsonSerializer.Deserialize<List<TradeLockerPositionInfo>>(body, JsonOptions);
            if (positions == null) return list;
            foreach (var p in positions)
            {
                var symbol = ResolveInstrumentIdToSymbol(p.TradableInstrumentId) ?? p.TradableInstrumentId.ToString();
                list.Add(new Position
                {
                    Symbol = symbol,
                    Quantity = (int)Math.Round(p.Qty),
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

    /// <summary>Phase 2: Stub. Volle Order-Ausführung in Phase 4.</summary>
    public Task<bool> PlaceOrderAsync(string symbol, TradeAction action, int quantity, CancellationToken ct = default)
        => Task.FromResult(false);
}

