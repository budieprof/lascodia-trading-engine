using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Backfills realised conformal coverage fields on legacy prediction logs so the conformal
/// breaker can audit older rows using the same canonical fields as new predictions.
/// </summary>
public sealed class MLConformalCoverageBackfillWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MLConformalBreakerOptions _options;
    private readonly ILogger<MLConformalCoverageBackfillWorker> _logger;

    public MLConformalCoverageBackfillWorker(
        IServiceScopeFactory scopeFactory,
        MLConformalBreakerOptions options,
        ILogger<MLConformalCoverageBackfillWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLConformalCoverageBackfillWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { break; }
            catch (Exception ex)
            { _logger.LogError(ex, "MLConformalCoverageBackfillWorker error"); }

            await Task.Delay(
                TimeSpan.FromMinutes(Math.Clamp(_options.BackfillPollIntervalMinutes, 1, 24 * 60)),
                stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeCtx.GetDbContext();
        int batchSize = Math.Clamp(_options.BackfillBatchSize, 1, 10_000);

        var logs = await db.Set<MLModelPredictionLog>()
            .Where(l => !l.IsDeleted
                        && l.ActualDirection != null
                        && l.OutcomeRecordedAt != null
                        && (l.WasConformalCovered == null
                            || l.ConformalNonConformityScore == null
                            || l.ConformalThresholdUsed == null
                            || l.ConformalTargetCoverageUsed == null))
            .OrderBy(l => l.OutcomeRecordedAt)
            .ThenBy(l => l.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (logs.Count == 0)
            return;

        var modelIds = logs.Select(l => l.MLModelId).Distinct().ToArray();
        var calibrations = await db.Set<MLConformalCalibration>()
            .AsNoTracking()
            .Where(c => modelIds.Contains(c.MLModelId) && !c.IsDeleted)
            .OrderByDescending(c => c.CalibratedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync(ct);

        var calibrationByModelId = calibrations
            .GroupBy(c => c.MLModelId)
            .ToDictionary(g => g.Key, g => g.First());

        int updated = 0;
        foreach (var log in logs)
        {
            calibrationByModelId.TryGetValue(log.MLModelId, out var calibration);
            double threshold = log.ConformalThresholdUsed
                ?? calibration?.CoverageThreshold
                ?? 0.5;

            if (log.ConformalThresholdUsed is null && calibration is not null)
                log.ConformalThresholdUsed = calibration.CoverageThreshold;
            if (log.ConformalTargetCoverageUsed is null && calibration is not null)
                log.ConformalTargetCoverageUsed = calibration.TargetCoverage;
            if (log.MLConformalCalibrationId is null && calibration is not null)
                log.MLConformalCalibrationId = calibration.Id;

            log.ConformalNonConformityScore ??= MLFeatureHelper.ComputeLoggedConformalNonConformityScore(
                log,
                log.ActualDirection!.Value,
                threshold);

            log.WasConformalCovered ??=
                MLFeatureHelper.WasActualDirectionInConformalSet(
                    log.ConformalPredictionSetJson,
                    log.ActualDirection!.Value)
                ?? log.ConformalNonConformityScore <= threshold;

            updated++;
        }

        await writeCtx.SaveChangesAsync(ct);
        _logger.LogInformation("MLConformalCoverageBackfillWorker: backfilled {Count} prediction logs.", updated);
    }
}
