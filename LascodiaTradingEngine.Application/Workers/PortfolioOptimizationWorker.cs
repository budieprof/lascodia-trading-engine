using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Strategies.Services;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Daily portfolio-level allocation pass. Reads observed per-strategy returns over
/// the lookback window, computes per-strategy weights via the configured method
/// ("Kelly" / "HRP" / "EqualWeight"), persists one <see cref="PortfolioWeightSnapshot"/>
/// row per strategy, and lets position sizing read the latest snapshot.
/// </summary>
public sealed class PortfolioOptimizationWorker : BackgroundService
{
    private readonly ILogger<PortfolioOptimizationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private const string CK_Method        = "Portfolio:AllocationMethod";
    private const string CK_PollSeconds   = "Portfolio:RecomputeIntervalSeconds";
    private const string DefaultMethod    = "Kelly";
    private const int    DefaultPollSecs  = 86400;  // daily

    public PortfolioOptimizationWorker(
        ILogger<PortfolioOptimizationWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PortfolioOptimizationWorker starting");
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSecs;
            try
            {
                pollSecs = await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PortfolioOptimizationWorker: cycle error");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<int> RunCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var writeCtx  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readCtx   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var optimizer = scope.ServiceProvider.GetRequiredService<IPortfolioOptimizer>();
        var writeDb   = writeCtx.GetDbContext();
        var readDb    = readCtx.GetDbContext();

        string method = await GetStringAsync(readDb, CK_Method, DefaultMethod, ct);
        int pollSecs  = await GetIntAsync(readDb, CK_PollSeconds, DefaultPollSecs, ct);

        var allocations = await optimizer.ComputeAllocationsAsync(method, ct);
        if (allocations.Count == 0)
        {
            _logger.LogDebug("PortfolioOptimizationWorker: no active strategies — skipping cycle");
            return pollSecs;
        }

        var nowUtc = DateTime.UtcNow;
        foreach (var alloc in allocations)
        {
            writeDb.Set<PortfolioWeightSnapshot>().Add(new PortfolioWeightSnapshot
            {
                StrategyId       = alloc.StrategyId,
                AllocationMethod = alloc.AllocationMethod,
                Weight           = alloc.Weight,
                KellyFraction    = alloc.KellyFraction,
                ObservedSharpe   = alloc.ObservedSharpe,
                SampleSize       = alloc.SampleSize,
                ComputedAt       = nowUtc,
            });
        }
        await writeCtx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "PortfolioOptimizationWorker: persisted {Count} allocation snapshots (method={Method}, totalWeight={Total:F4})",
            allocations.Count, method, allocations.Sum(a => a.Weight));
        return pollSecs;
    }

    private static async Task<string> GetStringAsync(DbContext db, string key, string fallback, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>().AsNoTracking()
            .Where(c => c.Key == key && !c.IsDeleted).Select(c => c.Value).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw!;
    }

    private static async Task<int> GetIntAsync(DbContext db, string key, int fallback, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>().AsNoTracking()
            .Where(c => c.Key == key && !c.IsDeleted).Select(c => c.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
