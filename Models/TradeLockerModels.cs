using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeTradingBot.Models;

public class TradeLockerAuthResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>Ein Konto aus GET /auth/jwt/all-accounts.</summary>
public class TradeLockerAccountInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonConverter(typeof(AccNumConverter))]
    [JsonPropertyName("accNum")]
    public string AccNum { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("accountBalance")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal AccountBalance { get; set; }
}

/// <summary>Liest accNum als Zahl oder String aus der TradeLocker-API.</summary>
public class AccNumConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.TryGetInt32(out var n) ? n.ToString() : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString() ?? string.Empty;
        return string.Empty;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

public class TradeLockerInstrumentInfo
{
    public int Id { get; set; }
    public int TradableInstrumentId { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;
    [JsonIgnore]
    public string ResolvedSymbol => !string.IsNullOrWhiteSpace(Name) ? Name : Symbol;
    public List<TradeLockerRoute>? Routes { get; set; }
}

public class TradeLockerRoute
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
}

public class TradeLockerQuote
{
    public int TradableInstrumentId { get; set; }
    public decimal Bid { get; set; }
    public decimal Ask { get; set; }
}

public class TradeLockerOrderRequest
{
    public decimal Price { get; set; }
    public decimal Qty { get; set; }
    public int RouteId { get; set; }
    public string Side { get; set; } = "buy";
    public decimal? StopLoss { get; set; }
    public string? StopLossType { get; set; } = "absolute";
    public decimal? TakeProfit { get; set; }
    public string? TakeProfitType { get; set; } = "absolute";
    public decimal TrStopOffset { get; set; }
    public int TradableInstrumentId { get; set; }
    public string Type { get; set; } = "market";
    public string Validity { get; set; } = "IOC";
}

public class TradeLockerOrderResponse
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public class TradeLockerPositionInfo
{
    public string Id { get; set; } = string.Empty;
    public int TradableInstrumentId { get; set; }
    public string Side { get; set; } = "buy";
    public decimal Qty { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal MarketPrice { get; set; }
}

/// <summary>Response von GET /trade/accounts/{id}/details.</summary>
public class TradeLockerAccountDetails
{
    [JsonPropertyName("balance")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal Balance { get; set; }

    [JsonPropertyName("equity")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal Equity { get; set; }

    [JsonPropertyName("margin")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal Margin { get; set; }

    [JsonPropertyName("freeMargin")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal FreeMargin { get; set; }

    [JsonPropertyName("accountBalance")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal AccountBalance { get; set; }

    [JsonPropertyName("accountEquity")]
    [JsonConverter(typeof(DecimalOrStringConverter))]
    public decimal AccountEquity { get; set; }
}

/// <summary>Liest decimal als Zahl oder String aus der TradeLocker-API.</summary>
public class DecimalOrStringConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetDecimal();
        if (reader.TokenType == JsonTokenType.String && decimal.TryParse(reader.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return 0m;
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>Ein Candle aus GET /trade/history.</summary>
public class TradeLockerCandle
{
    [JsonPropertyName("open")]
    public decimal Open { get; set; }
    [JsonPropertyName("high")]
    public decimal High { get; set; }
    [JsonPropertyName("low")]
    public decimal Low { get; set; }
    [JsonPropertyName("close")]
    public decimal Close { get; set; }
    [JsonPropertyName("time")]
    public long Time { get; set; }

    [JsonPropertyName("o")]
    public decimal O { get => Open; set => Open = value; }
    [JsonPropertyName("h")]
    public decimal H { get => High; set => High = value; }
    [JsonPropertyName("l")]
    public decimal L { get => Low; set => Low = value; }
    [JsonPropertyName("c")]
    public decimal C { get => Close; set => Close = value; }
    [JsonPropertyName("t")]
    public long T { get => Time; set => Time = value; }
}
