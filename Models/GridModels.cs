using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace ClaudeTradingBot.Models;

/// <summary>Status eines Grid-Trading-Setups.</summary>
public enum GridStatus
{
    Active,
    Paused,
    Completed
}

/// <summary>Status eines einzelnen Grid-Levels.</summary>
public enum GridLevelStatus
{
    Pending,
    Filled,
    CounterFilled
}

/// <summary>
/// Persistierter Zustand eines Grid-Trading-Setups fuer ein Symbol.
/// Die einzelnen Levels werden als JSON in LevelsJson gespeichert.
/// </summary>
public class GridState
{
    [Key]
    public int Id { get; set; }
    public string AccountId { get; set; } = "default";
    public string Symbol { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,6)")]
    public decimal CenterPrice { get; set; }

    public double GridSpacingPips { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal LotSizePerLevel { get; set; }

    public GridStatus Status { get; set; } = GridStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Serialisierte Liste von GridLevel-Objekten.</summary>
    public string LevelsJson { get; set; } = "[]";

    /// <summary>Gesamter realisierter PnL dieses Grids.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal TotalPnL { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public List<GridLevel> GetLevels()
    {
        try { return JsonSerializer.Deserialize<List<GridLevel>>(LevelsJson, JsonOpts) ?? new(); }
        catch { return new(); }
    }

    public void SetLevels(List<GridLevel> levels)
    {
        LevelsJson = JsonSerializer.Serialize(levels, JsonOpts);
        UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Ein einzelnes Level im Grid. Negative Indizes = unterhalb Center (Buy),
/// positive Indizes = oberhalb Center (Sell).
/// </summary>
public class GridLevel
{
    /// <summary>Level-Index: negativ = Buy-Zone, positiv = Sell-Zone.</summary>
    public int Index { get; set; }

    /// <summary>Preis dieses Grid-Levels.</summary>
    public decimal Price { get; set; }

    /// <summary>buy oder sell.</summary>
    public string Side { get; set; } = "buy";

    public GridLevelStatus Status { get; set; } = GridLevelStatus.Pending;

    /// <summary>Broker-Position-ID wenn gefuellt.</summary>
    public string? BrokerPositionId { get; set; }

    /// <summary>Zeitpunkt der Fuellung.</summary>
    public DateTime? FilledAt { get; set; }

    /// <summary>Realisierter PnL nach Counter-Fill.</summary>
    public decimal? PnL { get; set; }
}

/// <summary>Grid-Trading-Konfiguration (Teil von RiskSettings).</summary>
public class GridSettings
{
    /// <summary>Grid-Trading aktivieren/deaktivieren.</summary>
    public bool Enabled { get; set; }

    /// <summary>Abstand zwischen Grid-Levels in Pips.</summary>
    public double GridSpacingPips { get; set; } = 20;

    /// <summary>Anzahl Grid-Levels oberhalb des Center-Preises (Sell-Levels).</summary>
    public int GridLevelsAbove { get; set; } = 5;

    /// <summary>Anzahl Grid-Levels unterhalb des Center-Preises (Buy-Levels).</summary>
    public int GridLevelsBelow { get; set; } = 5;

    /// <summary>Lot-Groesse pro Grid-Level.</summary>
    public decimal LotSizePerLevel { get; set; } = 0.01m;

    /// <summary>Maximale Anzahl aktiver Grids gleichzeitig.</summary>
    public int MaxActiveGrids { get; set; } = 3;

    /// <summary>Maximale Levels die pro Zyklus getriggert werden (Schutz vor Gaps).</summary>
    public int MaxLevelsPerCycle { get; set; } = 2;

    /// <summary>Mindestdauer in Minuten bevor ein Grid deaktiviert werden kann.</summary>
    public int MinGridDurationMinutes { get; set; } = 60;
}
