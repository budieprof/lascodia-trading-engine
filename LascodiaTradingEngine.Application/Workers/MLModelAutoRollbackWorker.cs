using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Closes the live-degradation loop for ML models: drift workers already detect when an
/// active model's calibration / accuracy / retrain-failure metrics breach thresholds, but
/// nothing today *acts* on that detection. This worker reads those detected signals,
/// retires the failing model, and reactivates its <see cref="MLModel.PreviousChampionModelId"/>
/// fallback. Decisions are audited via <see cref="LogDecisionCommand"/> so an operator
/// can trace every rollback later.
///
/// <para>
/// Triggers (any one is sufficient):
/// </para>
/// <list type="bullet">
/// <item><description><see cref="MLModel.ConsecutiveRetrainFailures"/> &gt;= configured limit (default 3).</description></item>
/// <item><description><see cref="MLModel.PlattCalibrationDrift"/> &gt; configured threshold (default 0.30 absolute).</description></item>
/// <item><description><see cref="MLModel.LiveDirectionAccuracy"/> &lt; configured floor (default 0.45) once at least
///   <see cref="MLModel.LiveTotalPredictions"/> &gt;= MinLivePredictions (default 50) have accumulated.</description></item>
/// </list>
///
/// <para>
/// Rollback skipped (logged but not actioned) when no <see cref="MLModel.PreviousChampionModelId"/>
/// exists for the failing model — there is nothing safe to fall back to. Operators must
/// manually intervene in that case.
/// </para>
/// </summary>
public sealed class MLModelAutoRollbackWorker : BackgroundService
{
    private readonly ILogger<MLModelAutoRollbackWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private const string CK_PollSeconds              = "MLAutoRollback:PollIntervalSeconds";
    private const string CK_MaxRetrainFailures        = "MLAutoRollback:MaxConsecutiveRetrainFailures";
    private const string CK_MaxCalibrationDrift       = "MLAutoRollback:MaxPlattCalibrationDrift";
    private const string CK_MinLiveDirectionAccuracy  = "MLAutoRollback:MinLiveDirectionAccuracy";
    private const string CK_MinLivePredictions        = "MLAutoRollback:MinLivePredictionsForAccuracyCheck";

    private const int     DefaultPollSeconds              = 300;     // 5 min
    private const int     DefaultMaxRetrainFailures        = 3;
    private const double  DefaultMaxCalibrationDrift       = 0.30;
    private const decimal DefaultMinLiveDirectionAccuracy  = 0.45m;
    private const int     DefaultMinLivePredictions        = 50;

    public MLModelAutoRollbackWorker(
        ILogger<MLModelAutoRollbackWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLModelAutoRollbackWorker starting");
        // Initial delay so app startup migrations + DI graph stabilise before we touch models.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSeconds;
            try
            {
                pollSecs = await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLModelAutoRollbackWorker: cycle error");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<int> RunCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db       = writeCtx.GetDbContext();

        int pollSecs                = await GetIntAsync(db, CK_PollSeconds, DefaultPollSeconds, ct);
        int maxRetrainFailures       = await GetIntAsync(db, CK_MaxRetrainFailures, DefaultMaxRetrainFailures, ct);
        double maxCalibrationDrift   = await GetDoubleAsync(db, CK_MaxCalibrationDrift, DefaultMaxCalibrationDrift, ct);
        decimal minLiveAccuracy      = (decimal)await GetDoubleAsync(db, CK_MinLiveDirectionAccuracy, (double)DefaultMinLiveDirectionAccuracy, ct);
        int minLivePredictions       = await GetIntAsync(db, CK_MinLivePredictions, DefaultMinLivePredictions, ct);

        // Pull active models that breach any degradation trigger AND have a champion to fall back to.
        var degraded = await db.Set<MLModel>()
            .Where(m => m.IsActive
                     && !m.IsDeleted
                     && m.PreviousChampionModelId != null
                     && (m.ConsecutiveRetrainFailures >= maxRetrainFailures
                         || (m.PlattCalibrationDrift != null && m.PlattCalibrationDrift > maxCalibrationDrift)
                         || (m.LiveDirectionAccuracy != null
                             && m.LiveTotalPredictions >= minLivePredictions
                             && m.LiveDirectionAccuracy < minLiveAccuracy)))
            .ToListAsync(ct);

        if (degraded.Count == 0) return pollSecs;

        // Also surface degraded-without-fallback cases — operators need to know.
        var orphans = await db.Set<MLModel>()
            .Where(m => m.IsActive
                     && !m.IsDeleted
                     && m.PreviousChampionModelId == null
                     && (m.ConsecutiveRetrainFailures >= maxRetrainFailures
                         || (m.PlattCalibrationDrift != null && m.PlattCalibrationDrift > maxCalibrationDrift)
                         || (m.LiveDirectionAccuracy != null
                             && m.LiveTotalPredictions >= minLivePredictions
                             && m.LiveDirectionAccuracy < minLiveAccuracy)))
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);
        foreach (var orphan in orphans)
        {
            _logger.LogWarning(
                "MLModelAutoRollback: degraded model {Id} ({Symbol}/{Timeframe}) has no PreviousChampionModelId — manual intervention required",
                orphan.Id, orphan.Symbol, orphan.Timeframe);
        }

        int rollbackCount = 0;
        foreach (var failing in degraded)
        {
            if (ct.IsCancellationRequested) break;

            var champion = await db.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == failing.PreviousChampionModelId!.Value
                                       && !m.IsDeleted, ct);
            if (champion is null)
            {
                _logger.LogWarning(
                    "MLModelAutoRollback: failing model {Id} references PreviousChampionModelId={ChampionId} which no longer exists (deleted?) — skipping",
                    failing.Id, failing.PreviousChampionModelId);
                continue;
            }

            string reason = BuildRollbackReason(failing, maxRetrainFailures, maxCalibrationDrift, minLiveAccuracy);

            failing.IsActive             = false;
            failing.DegradationRetiredAt = DateTime.UtcNow;
            champion.IsActive            = true;

            try
            {
                await writeCtx.SaveChangesAsync(ct);
                rollbackCount++;
                _logger.LogWarning(
                    "MLModelAutoRollback: rolled back model {FailingId} → champion {ChampionId} ({Symbol}/{Timeframe}). Reason: {Reason}",
                    failing.Id, champion.Id, failing.Symbol, failing.Timeframe, reason);

                await SafeLogDecisionAsync(mediator, failing.Id, reason, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "MLModelAutoRollback: concurrency conflict rolling back model {Id} — another process likely already actioned it",
                    failing.Id);
            }
        }

        if (rollbackCount > 0)
        {
            _logger.LogInformation(
                "MLModelAutoRollback: cycle complete — rolled back {Count} model(s)",
                rollbackCount);
        }
        return pollSecs;
    }

    private static string BuildRollbackReason(MLModel failing, int maxFailures, double maxDrift, decimal minAcc)
    {
        var parts = new List<string>();
        if (failing.ConsecutiveRetrainFailures >= maxFailures)
            parts.Add($"ConsecutiveRetrainFailures={failing.ConsecutiveRetrainFailures}>={maxFailures}");
        if (failing.PlattCalibrationDrift is { } drift && drift > maxDrift)
            parts.Add($"PlattCalibrationDrift={drift:F3}>{maxDrift:F2}");
        if (failing.LiveDirectionAccuracy is { } acc && acc < minAcc)
            parts.Add($"LiveDirectionAccuracy={acc:F3}<{minAcc:F2}");
        return string.Join(" | ", parts);
    }

    private static async Task SafeLogDecisionAsync(IMediator mediator, long modelId, string reason, CancellationToken ct)
    {
        try
        {
            await mediator.Send(new LogDecisionCommand
            {
                Source       = "MLModelAutoRollbackWorker",
                EntityType   = "MLModel",
                EntityId     = modelId,
                DecisionType = "AutoRollback",
                Outcome      = "Rolled back to PreviousChampionModelId",
                Reason       = reason,
            }, ct);
        }
        catch (Exception)
        {
            // Audit failures must never block the rollback itself.
        }
    }

    private static async Task<int> GetIntAsync(DbContext db, string key, int fallback, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>().AsNoTracking()
            .Where(c => c.Key == key && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
        return int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static async Task<double> GetDoubleAsync(DbContext db, string key, double fallback, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>().AsNoTracking()
            .Where(c => c.Key == key && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
        return double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
