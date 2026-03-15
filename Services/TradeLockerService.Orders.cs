using System.Net.Http.Json;
using System.Text.Json;
using ClaudeTradingBot.Models;

namespace ClaudeTradingBot.Services;

public partial class TradeLockerService
{
    public async Task<PlaceOrderResult> PlaceOrderAsync(string symbol, TradeAction action, decimal quantityLots, decimal? stopLoss, decimal? takeProfit, OrderType orderType = OrderType.Market, decimal? entryPrice = null, CancellationToken ct = default)
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
            var orderTypeStr = orderType switch
            {
                OrderType.Limit => "limit",
                OrderType.Stop => "stop",
                _ => "market"
            };
            var body = new TradeLockerOrderRequest
            {
                Price = orderType != OrderType.Market && entryPrice.HasValue ? entryPrice.Value : 0m,
                Qty = quantityLots,
                RouteId = tradeRouteId.Value,
                Side = side,
                StopLoss = stopLoss,
                StopLossType = stopLoss.HasValue ? "absolute" : null,
                TakeProfit = takeProfit,
                TakeProfitType = takeProfit.HasValue ? "absolute" : null,
                TrStopOffset = 0m,
                TradableInstrumentId = tradableInstrumentId.Value,
                Type = orderTypeStr,
                Validity = orderType != OrderType.Market ? "GTC" : "IOC"
            };
            using var client = GetHttpClient();
            var requestJson = JsonSerializer.Serialize(body, JsonOptions);
            _logger.LogDebug("TradeLocker PlaceOrder request: {Json}", requestJson);
            var response = await client.PostAsJsonAsync($"trade/accounts/{_accountId}/orders", body, JsonOptions, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("TradeLocker PlaceOrder response: {Status} – {Body}", (int)response.StatusCode, Truncate(responseBody, 400));
            if (!response.IsSuccessStatusCode)
            {
                HandleConnectionError(response.StatusCode, $"PlaceOrder({symbol})");
                _logger.LogWarning("TradeLocker PlaceOrder failed: {StatusCode} – {Body}", (int)response.StatusCode, Truncate(responseBody, 400));
                return new PlaceOrderResult { Success = false };
            }
            var orderResponse = JsonSerializer.Deserialize<TradeLockerOrderResponse>(responseBody, JsonOptions);
            var orderId = orderResponse?.Id;
            _logger.LogInformation("TradeLocker order placed: {Symbol} {Side} {Qty} Lots, orderId={OrderId}, response={Body}",
                symbol, side, quantityLots, orderId, Truncate(responseBody, 200));
            // Market-Orders werden sofort gefüllt – Position-ID ermitteln
            // Limit/Stop-Orders sind pending – keine Position-ID erwartet
            string? positionId = null;
            if (orderType == OrderType.Market)
            {
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
                HandleConnectionError(response.StatusCode, $"ClosePosition({positionId})");
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

    public async Task<List<BrokerPendingOrder>> GetPendingOrdersAsync(CancellationToken ct = default)
    {
        var list = new List<BrokerPendingOrder>();
        if (!_isConnected || string.IsNullOrEmpty(_accountId)) return list;
        await ThrottleAsync(ct);
        try
        {
            using var client = GetHttpClient();
            var response = await client.GetAsync($"trade/accounts/{_accountId}/orders", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("GetPendingOrders failed: {Status}", (int)response.StatusCode);
                return list;
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("GetPendingOrders raw (300 chars): {Body}", Truncate(body, 300));

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            System.Text.Json.JsonElement target = root;
            if (root.TryGetProperty("d", out var d)) target = d;

            if (!target.TryGetProperty("orders", out var ordersEl) ||
                ordersEl.ValueKind != System.Text.Json.JsonValueKind.Array)
                return list;

            foreach (var item in ordersEl.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Array || item.GetArrayLength() < 6)
                    continue;

                // Array-Format: [id, instrumentId, routeId, side, qty, price, type, ...]
                var orderId = item[0].ToString();
                var instId = int.TryParse(item[1].ToString(), out var iid) ? iid : 0;
                var side = item[3].ToString();
                var qty = decimal.TryParse(item[4].ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var q) ? q : 0m;
                var price = decimal.TryParse(item[5].ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0m;
                var type = item.GetArrayLength() > 6 ? item[6].ToString() : "unknown";

                var symbol = ResolveInstrumentIdToSymbol(instId) ?? $"UNKNOWN_{instId}";

                list.Add(new BrokerPendingOrder
                {
                    OrderId = orderId,
                    Symbol = symbol,
                    Side = side,
                    Qty = qty,
                    Price = price,
                    Type = type,
                    CreatedAt = DateTime.UtcNow
                });
            }

            _logger.LogInformation("GetPendingOrders: {Count} pending orders", list.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetPendingOrders failed");
        }
        return list;
    }

    public async Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default)
    {
        if (!_isConnected || string.IsNullOrEmpty(_accountId))
        {
            _logger.LogWarning("TradeLocker: Cannot cancel order – not connected or no account.");
            return false;
        }
        await ThrottleAsync(ct);
        try
        {
            using var client = GetHttpClient();
            var response = await client.DeleteAsync($"trade/orders/{orderId}?accountId={_accountId}", ct);
            var success = response.IsSuccessStatusCode;
            _logger.LogInformation("CancelOrder {OrderId}: {Result} ({Status})",
                orderId, success ? "OK" : "FAILED", (int)response.StatusCode);
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CancelOrder {OrderId} failed", orderId);
            return false;
        }
    }
}
