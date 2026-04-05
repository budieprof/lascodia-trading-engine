using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Aggregates TickRecord data into SpreadProfile rows (percentile spread statistics by
/// hour-of-day and day-of-week). Runs on a configurable interval (default: daily).
/// The resulting profiles power time-varying spread simulation in the backtest engine.
/// </summary>
public class SpreadProfileWorker : BackgroundService
{
    private readonly ILogger<SpreadProfileWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SpreadProfileOptions _options;
    private DateTime _lastRunUtc = DateTime.MinValue;

    public SpreadProfileWorker(
        ILogger<SpreadProfileWorker> logger,
        IServiceScopeFactory scopeFactory,
        SpreadProfileOptions options)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SpreadProfileWorker is disabled via configuration");
            return;
        }

        _logger.LogInformation("SpreadProfileWorker starting (interval: {Hours}h, aggregation: {Days}d)",
            _options.WorkerIntervalHours, _options.AggregationDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if ((DateTime.UtcNow - _lastRunUtc).TotalHours >= _options.WorkerIntervalHours)
                {
                    await AggregateSpreadProfilesAsync(stoppingToken);
                    _lastRunUtc = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SpreadProfileWorker error");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task AggregateSpreadProfilesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();

        var symbols = await readCtx.GetDbContext()
            .Set<CurrencyPair>()
            .Where(cp => !cp.IsDeleted)
            .Select(cp => cp.Symbol)
            .Distinct()
            .ToListAsync(ct);

        var cutoff = DateTime.UtcNow.AddDays(-_options.AggregationDays);
        int totalProfiles = 0;

        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var ticks = await readCtx.GetDbContext()
                    .Set<TickRecord>()
                    .Where(t => t.Symbol == symbol && !t.IsDeleted && t.TickTimestamp >= cutoff && t.Ask >= t.Bid)
                    .Select(t => new { t.TickTimestamp, Spread = t.Ask - t.Bid })
                    .ToListAsync(ct);

                if (ticks.Count == 0) continue;

                var groups = ticks.GroupBy(t => (Hour: t.TickTimestamp.Hour, Day: t.TickTimestamp.DayOfWeek));

                // Soft-delete existing profiles for this symbol
                var existing = await writeCtx.GetDbContext()
                    .Set<SpreadProfile>()
                    .Where(p => p.Symbol == symbol && !p.IsDeleted)
                    .ToListAsync(ct);

                foreach (var old in existing)
                    old.IsDeleted = true;

                var now = DateTime.UtcNow;

                foreach (var group in groups)
                {
                    var spreads = group.Select(t => t.Spread).OrderBy(s => s).ToList();
                    if (spreads.Count == 0) continue;

                    var profile = new SpreadProfile
                    {
                        Symbol = symbol,
                        HourUtc = group.Key.Hour,
                        DayOfWeek = group.Key.Day,
                        SpreadP25 = Percentile(spreads, 0.25m),
                        SpreadP50 = Percentile(spreads, 0.50m),
                        SpreadP75 = Percentile(spreads, 0.75m),
                        SpreadP95 = Percentile(spreads, 0.95m),
                        SpreadMean = spreads.Average(),
                        SampleCount = spreads.Count,
                        AggregatedFrom = cutoff,
                        AggregatedTo = now,
                        ComputedAt = now,
                        IsDeleted = false
                    };

                    await writeCtx.GetDbContext().Set<SpreadProfile>().AddAsync(profile, ct);
                    totalProfiles++;
                }

                // Also create all-day aggregate rows (DayOfWeek = null) per hour
                var hourGroups = ticks.GroupBy(t => t.TickTimestamp.Hour);
                foreach (var hourGroup in hourGroups)
                {
                    var spreads = hourGroup.Select(t => t.Spread).OrderBy(s => s).ToList();
                    if (spreads.Count == 0) continue;

                    var profile = new SpreadProfile
                    {
                        Symbol = symbol,
                        HourUtc = hourGroup.Key,
                        DayOfWeek = null,
                        SpreadP25 = Percentile(spreads, 0.25m),
                        SpreadP50 = Percentile(spreads, 0.50m),
                        SpreadP75 = Percentile(spreads, 0.75m),
                        SpreadP95 = Percentile(spreads, 0.95m),
                        SpreadMean = spreads.Average(),
                        SampleCount = spreads.Count,
                        AggregatedFrom = cutoff,
                        AggregatedTo = now,
                        ComputedAt = now,
                        IsDeleted = false
                    };

                    await writeCtx.GetDbContext().Set<SpreadProfile>().AddAsync(profile, ct);
                    totalProfiles++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SpreadProfileWorker: error processing symbol {Symbol}", symbol);
            }
        }

        await writeCtx.GetDbContext().SaveChangesAsync(ct);
        _logger.LogInformation("SpreadProfileWorker: aggregated {Count} profiles for {Symbols} symbols",
            totalProfiles, symbols.Count);
    }

    /// <summary>
    /// Computes the percentile value from a pre-sorted list using linear interpolation
    /// (equivalent to Excel PERCENTILE.INC).
    /// </summary>
    private static decimal Percentile(List<decimal> sorted, decimal percentile)
    {
        if (sorted.Count == 0) return 0m;
        if (sorted.Count == 1) return sorted[0];
        double rank = (double)percentile * (sorted.Count - 1);
        int lower = (int)Math.Floor(rank);
        int upper = Math.Min(lower + 1, sorted.Count - 1);
        double fraction = rank - lower;
        return sorted[lower] + (decimal)fraction * (sorted[upper] - sorted[lower]);
    }
}
