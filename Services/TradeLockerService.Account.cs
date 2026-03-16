using System.Text.Json;
using ClaudeTradingBot.Models;

namespace ClaudeTradingBot.Services;

public partial class TradeLockerService
{
    // ── Konto-Details ────────────────────────────────────────────────────

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

    public async Task<AccountDetails> GetAccountDetailsAsync(CancellationToken ct = default)
    {
        // Cache pruefen
        if (_cachedAccountDetails != null && DateTime.UtcNow < _accountDetailsCacheExpiry)
            return _cachedAccountDetails;

        var result = new AccountDetails();

        // Versuch 1: /trade/accounts/{id}/details
        var details = await GetAccountDetailsInternalAsync(ct);
        if (details != null)
        {
            result.Balance = details.Balance != 0 ? details.Balance : details.AccountBalance;
            result.Equity = details.Equity != 0 ? details.Equity : details.AccountEquity;
            result.Margin = details.Margin;
            result.FreeMargin = details.FreeMargin;
        }

        // Versuch 2: /trade/accounts/{id}/state (alternativer Endpunkt bei manchen Brokern)
        if (result.Equity == 0 || result.Margin == 0)
        {
            var stateDetails = await GetAccountStateAsync(ct);
            if (stateDetails != null)
            {
                if (result.Balance == 0)
                    result.Balance = stateDetails.Balance != 0 ? stateDetails.Balance : stateDetails.AccountBalance;
                if (result.Equity == 0)
                    result.Equity = stateDetails.Equity != 0 ? stateDetails.Equity : stateDetails.AccountEquity;
                if (result.Margin == 0)
                    result.Margin = stateDetails.Margin;
                if (result.FreeMargin == 0)
                    result.FreeMargin = stateDetails.FreeMargin;
            }
        }

        // Fallback auf Balance aus all-accounts Login
        if (result.Balance == 0 && _balanceFromAllAccounts > 0)
            result.Balance = _balanceFromAllAccounts;
        if (result.Equity == 0 && result.Balance > 0)
            result.Equity = result.Balance;
        if (result.FreeMargin == 0 && result.Equity > 0)
            result.FreeMargin = result.Equity - result.Margin;

        _logger.LogDebug("AccountDetails: Balance={Balance}, Equity={Equity}, Margin={Margin}, FreeMargin={FreeMargin}",
            result.Balance, result.Equity, result.Margin, result.FreeMargin);

        _cachedAccountDetails = result;
        _accountDetailsCacheExpiry = DateTime.UtcNow + CacheTtl;
        return result;
    }

    /// <summary>Konto-Details (Balance, Equity). Viele Broker/Umgebungen liefern 404 – dann wird die Balance aus all-accounts verwendet.</summary>
    private async Task<TradeLockerAccountDetails?> GetAccountDetailsInternalAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_accountId)) return null;

        // Endpoint lieferte 404 – nie wieder versuchen
        if (_detailsEndpointIs404) return null;

        await ThrottleAsync(ct);
        try
        {
            using var client = GetHttpClient();
            var response = await client.GetAsync($"trade/accounts/{_accountId}/details", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _detailsEndpointIs404 = true;
                    _logger.LogDebug("TradeLocker: Endpunkt account/details nicht verfuegbar (404). Nutze Balance aus all-accounts.");
                }
                else
                    _logger.LogWarning("TradeLocker account details failed: Status {StatusCode}, Response: {Body}",
                        (int)response.StatusCode, Truncate(body, 500));
                return null;
            }
            _logger.LogDebug("TradeLocker account details raw response: {Body}", Truncate(body, 800));
            // Wrapper-Format erkennen: { "data": { ... } } oder { "accountDetails": { ... } }
            TradeLockerAccountDetails? details = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                var target = root;
                // TradeLocker nutzt verschiedene Wrapper: { "d": {...} }, { "data": {...} }, { "accountDetails": {...} }
                if (root.TryGetProperty("d", out var dProp) && dProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                    target = dProp;
                else if (root.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Object)
                    target = data;
                else if (root.TryGetProperty("accountDetails", out var ad) && ad.ValueKind == System.Text.Json.JsonValueKind.Object)
                    target = ad;

                // Felder können auch als Strings kommen, manuell parsen als Fallback
                details = JsonSerializer.Deserialize<TradeLockerAccountDetails>(target.GetRawText(), JsonOptions);

                // Manche APIs liefern die Daten in einem verschachtelten "accountDetails"-Objekt innerhalb von "d"
                if (details != null && details.Balance == 0 && details.Equity == 0 && details.AccountBalance == 0)
                {
                    if (target.TryGetProperty("accountDetails", out var nested) && nested.ValueKind == System.Text.Json.JsonValueKind.Object)
                        details = JsonSerializer.Deserialize<TradeLockerAccountDetails>(nested.GetRawText(), JsonOptions);
                }
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

    /// <summary>
    /// Alternativer Endpunkt: /trade/accounts/{id}/state
    /// Liefert { "d": { "accountDetailsData": [balance, equity, freeMargin, ..., margin, ...] } }
    /// Array-Indizes (TradeLocker Demo):
    ///   [0]=Balance, [1]=Equity, [2]=FreeMargin, [9]=UsedMargin
    /// </summary>
    private async Task<TradeLockerAccountDetails?> GetAccountStateAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_accountId)) return null;
        await ThrottleAsync(ct);
        try
        {
            using var client = GetHttpClient();
            var response = await client.GetAsync($"trade/accounts/{_accountId}/state", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TradeLocker /state endpoint: {Status}", (int)response.StatusCode);
                return null;
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("TradeLocker account state raw: {Body}", Truncate(body, 800));

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            var target = root;
            if (root.TryGetProperty("d", out var d))
                target = d;

            // Format: { "accountDetailsData": [balance, equity, freeMargin, ..., usedMargin, ...] }
            if (target.TryGetProperty("accountDetailsData", out var arr) &&
                arr.ValueKind == System.Text.Json.JsonValueKind.Array &&
                arr.GetArrayLength() >= 10)
            {
                var balance = arr[0].GetDecimal();
                var equity = arr[1].GetDecimal();
                var freeMargin = arr[2].GetDecimal();
                var usedMargin = arr[9].GetDecimal();

                return new TradeLockerAccountDetails
                {
                    Balance = balance,
                    Equity = equity,
                    FreeMargin = freeMargin,
                    Margin = usedMargin
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TradeLocker /state endpoint fehlgeschlagen");
            return null;
        }
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

            _logger.LogDebug("ParsePositions: {Count} Positionen geparst", result.Count);
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
        // Cache pruefen
        if (_cachedPositions != null && DateTime.UtcNow < _positionsCacheExpiry)
            return _cachedPositions;

        var list = new List<Position>();
        if (!_isConnected || string.IsNullOrEmpty(_accountId)) return list;
        await ThrottleAsync(ct);
        try
        {
            using var client = GetHttpClient();
            var response = await client.GetAsync($"trade/accounts/{_accountId}/positions", ct);
            if (!response.IsSuccessStatusCode)
            {
                HandleConnectionError(response.StatusCode, "GetPositions");
                return list;
            }
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

        _cachedPositions = list;
        _positionsCacheExpiry = DateTime.UtcNow + CacheTtl;
        return list;
    }
}
