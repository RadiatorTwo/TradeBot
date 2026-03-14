namespace ClaudeTradingBot.Services;

/// <summary>
/// Berechnet dynamische Korrelationen zwischen Watchlist-Symbolen basierend auf
/// historischen Close-Preisen (letzte 30 Tage, 1D-Candles). Aktualisiert taeglich
/// die CorrelationMatrix mit den berechneten Werten.
/// </summary>
public class CorrelationService : BackgroundService
{
    private readonly AccountManager _accountMgr;
    private readonly ISettingsRepository _settingsRepo;
    private readonly ILogger<CorrelationService> _logger;

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);
    private static readonly int LookbackDays = 30;

    /// <summary>Letzte berechnete Korrelationsmatrix (Symbol-Paar → Koeffizient).</summary>
    private Dictionary<(string, string), double> _dynamicCorrelations = new();

    /// <summary>Zeitpunkt der letzten Berechnung.</summary>
    public DateTime? LastCalculated { get; private set; }

    /// <summary>Anzahl berechneter Paare.</summary>
    public int PairCount => _dynamicCorrelations.Count;

    public CorrelationService(
        AccountManager accountMgr,
        ISettingsRepository settingsRepo,
        ILogger<CorrelationService> logger)
    {
        _accountMgr = accountMgr;
        _settingsRepo = settingsRepo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_accountMgr.HasAccounts)
                {
                    await CalculateCorrelationsAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CorrelationService: Fehler bei Korrelationsberechnung");
            }

            await Task.Delay(UpdateInterval, stoppingToken);
        }
    }

    private async Task CalculateCorrelationsAsync(CancellationToken ct)
    {
        var broker = _accountMgr.DefaultAccount?.EffectiveBroker;
        if (broker == null || !broker.IsConnected)
        {
            _logger.LogDebug("CorrelationService: Broker nicht verbunden, ueberspringe");
            return;
        }

        // Alle Symbole aus allen Accounts sammeln
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ctx in _accountMgr.Accounts)
        {
            foreach (var s in ctx.WatchList)
                symbols.Add(s);
        }

        // Fallback auf globale Watchlist
        if (symbols.Count == 0)
        {
            var globalWatchList = await _settingsRepo.GetGlobalWatchListAsync();
            foreach (var s in globalWatchList)
                symbols.Add(s);
        }

        if (symbols.Count < 2)
        {
            _logger.LogDebug("CorrelationService: Weniger als 2 Symbole, ueberspringe");
            return;
        }

        _logger.LogInformation("CorrelationService: Berechne Korrelationen fuer {Count} Symbole", symbols.Count);

        var from = DateTime.UtcNow.AddDays(-LookbackDays);
        var to = DateTime.UtcNow;

        // Close-Preise laden
        var priceData = new Dictionary<string, List<decimal>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
        {
            try
            {
                var candles = await broker.GetHistoricalCandlesAsync(symbol, "1D", from, to, ct);
                if (candles.Count >= 10)
                {
                    priceData[symbol] = candles.OrderBy(c => c.Time).Select(c => c.Close).ToList();
                }
                else
                {
                    _logger.LogDebug("CorrelationService: Zu wenig Candles fuer {Symbol} ({Count})", symbol, candles.Count);
                }

                // Rate-Limiting
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CorrelationService: Candles fuer {Symbol} konnten nicht geladen werden", symbol);
            }
        }

        if (priceData.Count < 2)
        {
            _logger.LogWarning("CorrelationService: Preisdaten fuer weniger als 2 Symbole verfuegbar");
            return;
        }

        // Pearson-Korrelation fuer alle Paare berechnen
        var newCorrelations = new Dictionary<(string, string), double>();
        var symbolList = priceData.Keys.ToList();

        for (int i = 0; i < symbolList.Count; i++)
        {
            for (int j = i + 1; j < symbolList.Count; j++)
            {
                var s1 = symbolList[i].ToUpperInvariant();
                var s2 = symbolList[j].ToUpperInvariant();

                var returns1 = CalculateReturns(priceData[symbolList[i]]);
                var returns2 = CalculateReturns(priceData[symbolList[j]]);

                // Auf gleiche Laenge trimmen
                var minLen = Math.Min(returns1.Count, returns2.Count);
                if (minLen < 5) continue;

                var r1 = returns1.TakeLast(minLen).ToList();
                var r2 = returns2.TakeLast(minLen).ToList();

                var correlation = PearsonCorrelation(r1, r2);
                if (!double.IsNaN(correlation))
                {
                    newCorrelations[(s1, s2)] = Math.Round(correlation, 2);
                }
            }
        }

        // Statische Matrix mit dynamischen Werten aktualisieren
        Models.CorrelationMatrix.UpdateDynamic(newCorrelations);
        _dynamicCorrelations = newCorrelations;
        LastCalculated = DateTime.UtcNow;

        _logger.LogInformation(
            "CorrelationService: {Count} Korrelationspaare berechnet. Beispiele: {Examples}",
            newCorrelations.Count,
            string.Join(", ", newCorrelations.Take(3).Select(kv => $"{kv.Key.Item1}/{kv.Key.Item2}={kv.Value:F2}")));
    }

    /// <summary>Gibt die aktuelle Korrelationsmatrix als flache Liste zurueck (fuer UI).</summary>
    public List<CorrelationEntry> GetCorrelationEntries()
    {
        return _dynamicCorrelations
            .Select(kv => new CorrelationEntry
            {
                Symbol1 = kv.Key.Item1,
                Symbol2 = kv.Key.Item2,
                Correlation = kv.Value
            })
            .OrderByDescending(e => Math.Abs(e.Correlation))
            .ToList();
    }

    private static List<double> CalculateReturns(List<decimal> prices)
    {
        var returns = new List<double>(prices.Count - 1);
        for (int i = 1; i < prices.Count; i++)
        {
            if (prices[i - 1] != 0)
                returns.Add((double)((prices[i] - prices[i - 1]) / prices[i - 1]));
        }
        return returns;
    }

    private static double PearsonCorrelation(List<double> x, List<double> y)
    {
        var n = x.Count;
        if (n < 2) return double.NaN;

        var avgX = x.Average();
        var avgY = y.Average();

        double sumXY = 0, sumX2 = 0, sumY2 = 0;
        for (int i = 0; i < n; i++)
        {
            var dx = x[i] - avgX;
            var dy = y[i] - avgY;
            sumXY += dx * dy;
            sumX2 += dx * dx;
            sumY2 += dy * dy;
        }

        var denominator = Math.Sqrt(sumX2 * sumY2);
        return denominator > 0 ? sumXY / denominator : 0;
    }
}

/// <summary>Ein Eintrag der Korrelationsmatrix fuer die UI-Darstellung.</summary>
public class CorrelationEntry
{
    public string Symbol1 { get; set; } = string.Empty;
    public string Symbol2 { get; set; } = string.Empty;
    public double Correlation { get; set; }
}
