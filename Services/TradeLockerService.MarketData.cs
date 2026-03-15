using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeTradingBot.Models;

namespace ClaudeTradingBot.Services;

public partial class TradeLockerService
{
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
            _logger.LogDebug("TradeLocker loaded {Count} instruments. Beispiele: {Samples}",
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
                HandleConnectionError(response.StatusCode, $"GetBidAsk({symbol})");
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

    public async Task<List<OhlcCandle>> GetHistoricalCandlesAsync(string symbol, string resolution, DateTime from, DateTime to, CancellationToken ct = default)
    {
        // Berechne benoetigte Candle-Anzahl aus dem Datumsbereich
        var resolutionHours = resolution switch
        {
            "1D" => 24.0,
            "4H" => 4.0,
            "1H" => 1.0,
            "15" or "15m" => 0.25,
            _ => 1.0
        };
        var totalHours = (to - from).TotalHours;
        var estimatedCandles = (int)Math.Ceiling(totalHours / resolutionHours);

        if (estimatedCandles <= 0)
        {
            _logger.LogWarning("GetHistoricalCandles: Ungueltige Zeitspanne {From} – {To}", from, to);
            return new List<OhlcCandle>();
        }

        _logger.LogDebug(
            "GetHistoricalCandles: {Symbol} {Res} von {From:dd.MM.yyyy} bis {To:dd.MM.yyyy} (~{Count} Candles erwartet)",
            symbol, resolution, from, to, estimatedCandles);

        // Nutze die bewährte GetCandlesInternalAsync in Chunks
        if (!_isConnected) return new List<OhlcCandle>();
        var instrumentId = ResolveSymbolToInstrumentId(symbol);
        if (instrumentId == null)
        {
            _logger.LogWarning("GetHistoricalCandles: Symbol {Symbol} nicht gefunden", symbol);
            return new List<OhlcCandle>();
        }

        var result = new List<OhlcCandle>();
        var resolutionMs = (long)(resolutionHours * 60 * 60 * 1000);
        var chunkSize = 500;
        var fromMs = new DateTimeOffset(from, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(to, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var currentFrom = fromMs;
        var chunkCount = 0;
        while (currentFrom < toMs && !ct.IsCancellationRequested)
        {
            var currentTo = Math.Min(currentFrom + chunkSize * resolutionMs, toMs);
            chunkCount++;

            await ThrottleAsync(ct);
            try
            {
                var routeId = GetInfoRouteId(instrumentId.Value);
                if (routeId == null)
                {
                    _logger.LogWarning("GetHistoricalCandles: Keine INFO-Route fuer {Symbol}", symbol);
                    break;
                }

                var url = $"trade/history?tradableInstrumentId={instrumentId.Value}&routeId={routeId}&resolution={resolution}&from={currentFrom}&to={currentTo}";
                using var client = GetHttpClient();
                var response = await client.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "GetHistoricalCandles Chunk {Chunk} fehlgeschlagen: {Status} – {Body}",
                        chunkCount, (int)response.StatusCode, Truncate(errBody, 300));
                    HandleConnectionError(response.StatusCode, $"GetHistoricalCandles({symbol})");
                    break;
                }

                var body = await response.Content.ReadAsStringAsync(ct);

                // TradeLocker gibt {"s":"no_data"} wenn keine Daten verfuegbar
                if (body.Contains("\"no_data\"", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(
                        "GetHistoricalCandles Chunk {Chunk}: API meldet 'no_data'. " +
                        "TradeLocker liefert moeglicherweise keine historischen Daten wenn der Markt geschlossen ist (Wochenende).",
                        chunkCount);

                    if (result.Count == 0)
                    {
                        // Erster Chunk hat keine Daten — abbrechen
                        break;
                    }
                    // Spaetere Chunks: Luecke moeglich, weiter versuchen
                    currentFrom = currentTo;
                    continue;
                }

                var parsed = ParseCandles(body);
                if (parsed != null && parsed.Count > 0)
                {
                    result.AddRange(parsed.Select(c => new OhlcCandle
                    {
                        Open = c.Open, High = c.High, Low = c.Low, Close = c.Close, Time = c.Time
                    }));
                    _logger.LogDebug("GetHistoricalCandles Chunk {Chunk}: {Count} Candles geladen", chunkCount, parsed.Count);
                }
                else
                {
                    _logger.LogDebug("GetHistoricalCandles Chunk {Chunk}: Keine Candles, Response (200c): {Body}",
                        chunkCount, Truncate(body, 200));
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetHistoricalCandles Chunk {Chunk} Exception fuer {Symbol}", chunkCount, symbol);
                break;
            }

            currentFrom = currentTo;
        }

        // Deduplizieren und sortieren
        var final = result
            .GroupBy(c => c.Time)
            .Select(g => g.First())
            .OrderBy(c => c.Time)
            .ToList();

        _logger.LogDebug("GetHistoricalCandles: {Symbol} fertig, {Count} Candles total", symbol, final.Count);
        return final;
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
}
