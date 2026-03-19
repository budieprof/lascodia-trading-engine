using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Conformal Temporal Circuit Breaker. Identifies contiguous runs where an active model is
/// systematically over-confident and wrong (20 or more consecutive incorrect prediction logs),
/// then suspends signal generation by setting <c>IsSuppressed = true</c> on the model.
/// </summary>
/// <remarks>
/// When the model accumulates 20+ consecutive incorrect predictions it indicates a structural
/// regime shift that the model has not adapted to. The suspension duration is set to
/// 2× the run length, proxy-mapped at 4 hours per bar, giving the market time to normalise.
/// Active breakers whose <c>ResumeAt</c> has passed are automatically cleared each cycle.
/// Runs daily.
/// </remarks>
public sealed class MLConformalBreakerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLConformalBreakerWorker> _logger;

    private static readonly TimeSpan _interval     = TimeSpan.FromDays(1);
    private static readonly TimeSpan _initialDelay = TimeSpan.FromMinutes(35);

    private const int MaxLogs          = 100;
    private const int MinLogs          = 30;
    private const int TriggerRunLength = 20;

    public MLConformalBreakerWorker(IServiceScopeFactory scopeFactory, ILogger<MLConformalBreakerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLConformalBreakerWorker started.");
        await Task.Delay(_initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLConformalBreakerWorker error"); }
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        var now = DateTime.UtcNow;

        // Step 1 — Clear expired breakers
        var expiredBreakers = await writeDb.Set<MLConformalBreakerLog>()
            .Where(b => b.IsActive && b.ResumeAt <= now)
            .ToListAsync(ct);

        foreach (var breaker in expiredBreakers)
        {
            breaker.IsActive = false;
            var suppressed = await writeDb.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == breaker.MLModelId, ct);
            if (suppressed is not null)
                suppressed.IsSuppressed = false;

            _logger.LogInformation(
                "MLConformalBreakerWorker: RESUMED {Symbol}/{Timeframe} — breaker expired.",
                breaker.Symbol, breaker.Timeframe);
        }

        // Step 2 — Evaluate active models
        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            var logs = await readDb.Set<MLModelPredictionLog>()
                .Where(l => l.MLModelId == model.Id
                            && l.DirectionCorrect != null
                            && !l.IsDeleted)
                .OrderBy(l => l.PredictedAt)
                .Take(MaxLogs)
                .ToListAsync(ct);

            if (logs.Count < MinLogs) continue;

            // Find the longest run of consecutive incorrect predictions
            int maxRun     = 0;
            int currentRun = 0;
            foreach (var log in logs)
            {
                if (log.DirectionCorrect == false)
                {
                    currentRun++;
                    if (currentRun > maxRun) maxRun = currentRun;
                }
                else
                {
                    currentRun = 0;
                }
            }

            int totalCorrect = logs.Count(l => l.DirectionCorrect == true);
            double empiricalCoverage = totalCorrect / (double)logs.Count;

            if (maxRun < TriggerRunLength) continue;

            int suspensionBars = maxRun * 2;
            var resumeAt       = now.AddHours(suspensionBars * 4.0);

            _logger.LogWarning(
                "MLConformalBreakerWorker: SUSPENDED {S}/{T} — {N} consecutive poor coverage bars",
                model.Symbol, model.Timeframe, maxRun);

            // Upsert MLConformalBreakerLog
            var existing = await writeDb.Set<MLConformalBreakerLog>()
                .FirstOrDefaultAsync(
                    b => b.MLModelId == model.Id && b.IsActive,
                    ct);

            if (existing is not null)
            {
                existing.ConsecutivePoorCoverageBars = maxRun;
                existing.EmpiricalCoverage           = empiricalCoverage;
                existing.SuspensionBars              = suspensionBars;
                existing.SuspendedAt                 = now;
                existing.ResumeAt                    = resumeAt;
            }
            else
            {
                await writeDb.Set<MLConformalBreakerLog>().AddAsync(new MLConformalBreakerLog
                {
                    MLModelId                    = model.Id,
                    Symbol                       = model.Symbol,
                    Timeframe                    = model.Timeframe,
                    ConsecutivePoorCoverageBars  = maxRun,
                    EmpiricalCoverage            = empiricalCoverage,
                    SuspensionBars               = suspensionBars,
                    SuspendedAt                  = now,
                    ResumeAt                     = resumeAt,
                    IsActive                     = true
                }, ct);
            }

            // Suppress the model
            var writeModel = await writeDb.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == model.Id, ct);
            if (writeModel is not null)
                writeModel.IsSuppressed = true;
        }

        await writeDb.SaveChangesAsync(ct);
        _logger.LogInformation("MLConformalBreakerWorker: cycle complete.");
    }
}
