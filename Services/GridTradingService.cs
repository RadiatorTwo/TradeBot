using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ClaudeTradingBot.Services;

/// <summary>
/// Verwaltet Grid-Trading-Setups: Initialisierung, Level-Tracking, Order-Platzierung,
/// Counter-Orders und Grid-Refill. Arbeitet pro Account.
/// </summary>
public class GridTradingService
{
    private readonly IBrokerService _broker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GridTradingService> _logger;

    public string AccountId { get; init; } = "default";

    public GridTradingService(
        IBrokerService broker,
        IServiceScopeFactory scopeFactory,
        ILogger<GridTradingService> logger)
    {
        _broker = broker;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Initialisiert ein neues Grid fuer ein Symbol. Erstellt Levels ober-/unterhalb des Center-Preises.
    /// </summary>
    public async Task<GridState> InitializeGridAsync(
        string symbol, decimal centerPrice, GridSettings settings, CancellationToken ct)
    {
        var levels = new List<GridLevel>();
        var pipSize = PipCalculator.GetPipSize(symbol);
        var spacingPrice = (decimal)settings.GridSpacingPips * pipSize;

        // Buy-Levels unterhalb des Centers
        for (int i = 1; i <= settings.GridLevelsBelow; i++)
        {
            levels.Add(new GridLevel
            {
                Index = -i,
                Price = Math.Round(centerPrice - spacingPrice * i, 6),
                Side = "buy",
                Status = GridLevelStatus.Pending
            });
        }

        // Sell-Levels oberhalb des Centers
        for (int i = 1; i <= settings.GridLevelsAbove; i++)
        {
            levels.Add(new GridLevel
            {
                Index = i,
                Price = Math.Round(centerPrice + spacingPrice * i, 6),
                Side = "sell",
                Status = GridLevelStatus.Pending
            });
        }

        var grid = new GridState
        {
            AccountId = AccountId,
            Symbol = symbol,
            CenterPrice = centerPrice,
            GridSpacingPips = settings.GridSpacingPips,
            LotSizePerLevel = settings.LotSizePerLevel,
            Status = GridStatus.Active
        };
        grid.SetLevels(levels);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.GridStates.Add(grid);
        await db.SaveChangesAsync(ct);

        await LogAsync("Info", $"Grid initialisiert: {symbol} Center={centerPrice:F5}, " +
            $"{settings.GridLevelsBelow} Buy + {settings.GridLevelsAbove} Sell Levels, " +
            $"Spacing={settings.GridSpacingPips} Pips, Lot={settings.LotSizePerLevel}");

        _logger.LogInformation(
            "[{AccountId}] Grid initialisiert: {Symbol} Center={Center:F5}, {Total} Levels",
            AccountId, symbol, centerPrice, levels.Count);

        return grid;
    }

    /// <summary>
    /// Verwaltet ein aktives Grid: prueft getriggerte Levels, platziert Orders, Counter-Fills.
    /// Wird pro Trading-Zyklus aufgerufen.
    /// </summary>
    public async Task ManageGridAsync(string symbol, GridSettings settings, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var grid = await db.GridStates
            .FirstOrDefaultAsync(g => g.AccountId == AccountId
                && g.Symbol == symbol
                && g.Status == GridStatus.Active, ct);

        if (grid == null) return;

        var currentPrice = await _broker.GetCurrentPriceAsync(symbol, ct);
        if (currentPrice == 0) return;

        var levels = grid.GetLevels();
        var spacingPrice = (decimal)grid.GridSpacingPips * PipCalculator.GetPipSize(symbol);
        var levelsTriggeredThisCycle = 0;
        var changed = false;

        foreach (var level in levels)
        {
            if (ct.IsCancellationRequested) break;

            switch (level.Status)
            {
                case GridLevelStatus.Pending:
                    // Pruefen ob Preis das Level erreicht hat
                    var triggered = level.Side == "buy"
                        ? currentPrice <= level.Price
                        : currentPrice >= level.Price;

                    if (triggered && levelsTriggeredThisCycle < settings.MaxLevelsPerCycle)
                    {
                        var action = level.Side == "buy" ? TradeAction.Buy : TradeAction.Sell;
                        var result = await _broker.PlaceOrderAsync(
                            symbol, action, grid.LotSizePerLevel, null, null, ct: ct);

                        if (result.Success)
                        {
                            level.Status = GridLevelStatus.Filled;
                            level.BrokerPositionId = result.BrokerPositionId;
                            level.FilledAt = DateTime.UtcNow;
                            levelsTriggeredThisCycle++;
                            changed = true;

                            _logger.LogInformation(
                                "[{AccountId}] Grid {Symbol}: Level {Index} ({Side}) getriggert @ {Price:F5}",
                                AccountId, symbol, level.Index, level.Side, level.Price);

                            await LogAsync("Info",
                                $"Grid {symbol}: {level.Side.ToUpper()} Level {level.Index} getriggert @ {level.Price:F5}");
                        }
                    }
                    break;

                case GridLevelStatus.Filled:
                    // Counter-Fill pruefen: Preis hat sich um 1 Grid-Spacing in Gegenrichtung bewegt
                    var counterReached = level.Side == "buy"
                        ? currentPrice >= level.Price + spacingPrice
                        : currentPrice <= level.Price - spacingPrice;

                    if (counterReached && level.BrokerPositionId != null)
                    {
                        var closeSuccess = await _broker.ClosePositionAsync(
                            level.BrokerPositionId, null, ct);

                        if (closeSuccess)
                        {
                            var pnl = level.Side == "buy"
                                ? (currentPrice - level.Price) * grid.LotSizePerLevel
                                : (level.Price - currentPrice) * grid.LotSizePerLevel;

                            level.Status = GridLevelStatus.CounterFilled;
                            level.PnL = pnl;
                            grid.TotalPnL += pnl;
                            changed = true;

                            _logger.LogInformation(
                                "[{AccountId}] Grid {Symbol}: Level {Index} Counter-Fill @ {Price:F5}, PnL={PnL:+0.00;-0.00}",
                                AccountId, symbol, level.Index, currentPrice, pnl);

                            await LogAsync("Info",
                                $"Grid {symbol}: Level {level.Index} Counter-Fill @ {currentPrice:F5}, PnL: {pnl:+0.00;-0.00}");
                        }
                    }
                    break;

                case GridLevelStatus.CounterFilled:
                    // Refill: Level zuruecksetzen auf Pending
                    level.Status = GridLevelStatus.Pending;
                    level.BrokerPositionId = null;
                    level.FilledAt = null;
                    level.PnL = null;
                    changed = true;
                    break;
            }
        }

        if (changed)
        {
            grid.SetLevels(levels);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Verwaltet alle aktiven Grids fuer diesen Account.</summary>
    public async Task ManageAllActiveGridsAsync(GridSettings settings, CancellationToken ct)
    {
        if (!settings.Enabled) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var activeSymbols = await db.GridStates
            .Where(g => g.AccountId == AccountId && g.Status == GridStatus.Active)
            .Select(g => g.Symbol)
            .ToListAsync(ct);

        foreach (var symbol in activeSymbols)
        {
            try
            {
                await ManageGridAsync(symbol, settings, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{AccountId}] Grid-Management fehlgeschlagen fuer {Symbol}", AccountId, symbol);
            }
        }
    }

    /// <summary>
    /// Behandelt eine Grid-Empfehlung vom LLM: Initialisiert ein neues Grid oder verwaltet ein bestehendes.
    /// </summary>
    public async Task HandleGridRecommendationAsync(
        string symbol, decimal currentPrice, ClaudeTradeRecommendation recommendation,
        GridSettings settings, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var existingGrid = await db.GridStates
            .FirstOrDefaultAsync(g => g.AccountId == AccountId
                && g.Symbol == symbol
                && g.Status == GridStatus.Active, ct);

        if (existingGrid != null)
        {
            _logger.LogDebug("[{AccountId}] Grid fuer {Symbol} bereits aktiv, verwalte bestehendes Grid", AccountId, symbol);
            await ManageGridAsync(symbol, settings, ct);
            return;
        }

        // Maximale Grids pruefen
        var activeCount = await db.GridStates
            .CountAsync(g => g.AccountId == AccountId && g.Status == GridStatus.Active, ct);

        if (activeCount >= settings.MaxActiveGrids)
        {
            _logger.LogInformation(
                "[{AccountId}] Max. aktive Grids erreicht ({Count}/{Max}), kein neues Grid fuer {Symbol}",
                AccountId, activeCount, settings.MaxActiveGrids, symbol);
            return;
        }

        var centerPrice = recommendation.GridCenterPrice ?? currentPrice;
        await InitializeGridAsync(symbol, centerPrice, settings, ct);
    }

    /// <summary>Deaktiviert ein Grid und schliesst optional alle offenen Grid-Positionen.</summary>
    public async Task DeactivateGridAsync(string symbol, bool closePositions, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var grid = await db.GridStates
            .FirstOrDefaultAsync(g => g.AccountId == AccountId
                && g.Symbol == symbol
                && g.Status == GridStatus.Active, ct);

        if (grid == null) return;

        if (closePositions)
        {
            var levels = grid.GetLevels();
            foreach (var level in levels.Where(l => l.Status == GridLevelStatus.Filled && l.BrokerPositionId != null))
            {
                try
                {
                    await _broker.ClosePositionAsync(level.BrokerPositionId!, null, ct);
                    _logger.LogInformation(
                        "[{AccountId}] Grid {Symbol}: Position Level {Index} geschlossen bei Deaktivierung",
                        AccountId, symbol, level.Index);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[{AccountId}] Grid {Symbol}: Konnte Position Level {Index} nicht schliessen",
                        AccountId, symbol, level.Index);
                }
            }
        }

        grid.Status = GridStatus.Completed;
        grid.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await LogAsync("Info", $"Grid deaktiviert: {symbol}, TotalPnL: {grid.TotalPnL:+0.00;-0.00}");
        _logger.LogInformation("[{AccountId}] Grid deaktiviert: {Symbol}, PnL={PnL:+0.00;-0.00}",
            AccountId, symbol, grid.TotalPnL);
    }

    /// <summary>Prueft ob ein aktives Grid fuer ein Symbol existiert.</summary>
    public async Task<bool> HasActiveGridAsync(string symbol, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        return await db.GridStates.AnyAsync(g =>
            g.AccountId == AccountId && g.Symbol == symbol && g.Status == GridStatus.Active, ct);
    }

    /// <summary>
    /// Recovery nach Neustart: Gleicht Grid-Levels mit Broker-Positionen ab.
    /// </summary>
    public async Task RecoverGridsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var activeGrids = await db.GridStates
            .Where(g => g.AccountId == AccountId && g.Status == GridStatus.Active)
            .ToListAsync(ct);

        if (activeGrids.Count == 0) return;

        var brokerPositions = await _broker.GetPositionsAsync(ct);
        var brokerPosIds = brokerPositions
            .Where(p => p.BrokerPositionId != null)
            .Select(p => p.BrokerPositionId!)
            .ToHashSet();

        foreach (var grid in activeGrids)
        {
            var levels = grid.GetLevels();
            var recoveredCount = 0;
            var orphanedCount = 0;

            foreach (var level in levels.Where(l => l.Status == GridLevelStatus.Filled))
            {
                if (level.BrokerPositionId != null && brokerPosIds.Contains(level.BrokerPositionId))
                {
                    recoveredCount++;
                }
                else
                {
                    // Position wurde extern geschlossen (SL/TP/manuell)
                    level.Status = GridLevelStatus.CounterFilled;
                    orphanedCount++;
                }
            }

            if (orphanedCount > 0)
            {
                grid.SetLevels(levels);
                await db.SaveChangesAsync(ct);
            }

            _logger.LogInformation(
                "[{AccountId}] Grid Recovery {Symbol}: {Recovered} Positionen aktiv, {Orphaned} extern geschlossen",
                AccountId, grid.Symbol, recoveredCount, orphanedCount);
        }
    }

    private async Task LogAsync(string level, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.TradingLogs.Add(new TradingLog
        {
            AccountId = AccountId,
            Level = level,
            Source = "GridTrading",
            Message = message
        });
        await db.SaveChangesAsync();
    }
}
