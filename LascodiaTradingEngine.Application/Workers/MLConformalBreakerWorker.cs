using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Conformal Temporal Circuit Breaker. Monitors realised conformal coverage for active models
/// and temporarily suppresses models whose prediction sets repeatedly fail to contain the
/// eventual outcome.
/// </summary>
/// <remarks>
/// A resolved prediction is covered when its stored nonconformity score is less than or equal
/// to the model's current conformal coverage threshold. Consecutive uncovered outcomes indicate
/// that the model's uncertainty estimate is no longer calibrated to the live market regime.
///
/// Runs daily with a 35-minute initial startup delay to avoid competing with startup-time
/// migrations and initial data loading.
/// </remarks>
public sealed class MLConformalBreakerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLConformalBreakerWorker> _logger;
    private readonly MLConformalBreakerOptions _options;
    private readonly TradingMetrics _metrics;
    private readonly IAlertDispatcher _alertDispatcher;
    private readonly IMLConformalCoverageEvaluator _coverageEvaluator;
    private readonly IMLConformalPredictionLogReader _predictionLogReader;
    private readonly IMLConformalBreakerStateStore _stateStore;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">Creates scoped DI scopes per run cycle.</param>
    /// <param name="logger">Structured logger for suspension and resumption events.</param>
    public MLConformalBreakerWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLConformalBreakerWorker> logger,
        MLConformalBreakerOptions options,
        TradingMetrics metrics,
        IAlertDispatcher alertDispatcher,
        IMLConformalCoverageEvaluator coverageEvaluator,
        IMLConformalPredictionLogReader predictionLogReader,
        IMLConformalBreakerStateStore stateStore)
    {
        _scopeFactory        = scopeFactory;
        _logger              = logger;
        _options             = options;
        _metrics             = metrics;
        _alertDispatcher     = alertDispatcher;
        _coverageEvaluator   = coverageEvaluator;
        _predictionLogReader = predictionLogReader;
        _stateStore          = stateStore;
    }

    /// <summary>
    /// Main hosted-service loop. Waits for the configured initial delay before starting, then
    /// evaluates all active models at the configured interval.
    /// </summary>
    /// <param name="stoppingToken">Signals graceful shutdown requested by the host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLConformalBreakerWorker started.");
        // Initial delay prevents race conditions with startup migrations and data loading.
        await Task.Delay(GetInitialDelay(), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { break; }
            catch (Exception ex)
            {
                _metrics.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", nameof(MLConformalBreakerWorker)));
                _logger.LogError(ex, "MLConformalBreakerWorker error");
            }
            await Task.Delay(GetInterval(), stoppingToken);
        }
    }

    /// <summary>
    /// Core circuit-breaker evaluation routine, executed once per daily cycle.
    ///
    /// <b>Phase 1 — Expiry sweep:</b> loads all active <see cref="MLConformalBreakerLog"/>
    /// records whose <c>ResumeAt</c> has passed and clears them, restoring the corresponding
    /// model's <c>IsSuppressed</c> flag to false.
    ///
    /// <b>Phase 2 — Model evaluation:</b> for each active base model with a valid conformal
    /// calibration, loads the most recent configured window of resolved conformal prediction
    /// logs and scans for the longest contiguous run of uncovered outcomes. If the run meets
    /// the configured consecutive-uncovered threshold, or if empirical coverage falls below
    /// the calibrated target minus tolerance, the model is suspended and a
    /// <see cref="MLConformalBreakerLog"/> is upserted.
    ///
    /// <b>Suspension duration:</b>
    ///   suspensionBars = severityBars × 2
    ///   resumeAt       = now + suspensionBars × actual model timeframe duration
    /// </summary>
    /// <param name="ct">Cooperative cancellation token.</param>
    internal async Task RunAsync(CancellationToken ct)
    {
        var cycleStart = Stopwatch.GetTimestamp();
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        var maxLogs = Math.Max(1, _options.MaxLogs);
        var minLogs = Math.Max(1, _options.MinLogs);
        var triggerRunLength = Math.Max(1, _options.ConsecutiveUncoveredTrigger);
        var coverageTolerance = Math.Clamp(_options.CoverageTolerance, 0.0, 1.0);
        var maxSuspensionBars = Math.Max(1, _options.MaxSuspensionBars);
        var alpha = Math.Clamp(_options.StatisticalAlpha, 1e-12, 0.49);
        var wilsonConfidence = Math.Clamp(_options.WilsonConfidenceLevel, 0.51, 0.999999);

        var activeModels = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer)
            .ToListAsync(ct);

        var modelIds = activeModels.Select(m => m.Id).ToArray();
        var activeBreakersByModelId = await readDb.Set<MLConformalBreakerLog>()
            .AsNoTracking()
            .Where(b => modelIds.Contains(b.MLModelId) && b.IsActive && !b.IsDeleted)
            .ToDictionaryAsync(b => b.MLModelId, ct);

        var latestCalibrations = await readDb.Set<MLConformalCalibration>()
            .AsNoTracking()
            .Where(c => modelIds.Contains(c.MLModelId) && !c.IsDeleted)
            .OrderByDescending(c => c.CalibratedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync(ct);

        var calibrationByModelId = latestCalibrations
            .GroupBy(c => c.MLModelId)
            .ToDictionary(g => g.Key, g => g.First());

        var logsByModelId = await _predictionLogReader.LoadRecentResolvedLogsByModelAsync(readDb, modelIds, maxLogs, ct);
        var tripCandidates = new List<BreakerTripCandidate>();
        var recoveryCandidates = new List<BreakerRecoveryCandidate>();
        var refreshCandidates = new List<BreakerRefreshCandidate>();
        int evaluated = 0;
        int skippedNoCalibration = 0;
        int skippedInsufficient = 0;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();
            if (!calibrationByModelId.TryGetValue(model.Id, out var calibration) ||
                !IsUsableCalibration(calibration, minLogs))
            {
                skippedNoCalibration++;
                _metrics.MLConformalBreakerModelsSkipped.Add(1,
                    new("reason", "no_usable_calibration"),
                    new("symbol", model.Symbol),
                    new("timeframe", model.Timeframe.ToString()));
                _logger.LogDebug(
                    "MLConformalBreakerWorker: skipped model {ModelId} {Symbol}/{Timeframe}; no usable conformal calibration.",
                    model.Id, model.Symbol, model.Timeframe);
                continue;
            }

            if (!logsByModelId.TryGetValue(model.Id, out var recentLogs))
                recentLogs = [];

            activeBreakersByModelId.TryGetValue(model.Id, out var activeBreaker);
            var observations = recentLogs
                .Where(l => activeBreaker is null || l.OutcomeRecordedAt > activeBreaker.SuspendedAt)
                .OrderBy(l => l.OutcomeRecordedAt)
                .ThenBy(l => l.Id)
                .Select(l => TryCreateObservation(l, calibration.CoverageThreshold))
                .Where(o => o.HasValue)
                .Select(o => o!.Value)
                .ToList();

            var evaluation = _coverageEvaluator.Evaluate(
                observations,
                new ConformalCoverageEvaluationOptions(
                    calibration.TargetCoverage,
                    coverageTolerance,
                    minLogs,
                    triggerRunLength,
                    _options.UseWilsonCoverageFloor,
                    wilsonConfidence,
                    alpha));

            if (!evaluation.HasEnoughSamples)
            {
                skippedInsufficient++;
                _metrics.MLConformalBreakerModelsSkipped.Add(1,
                    new("reason", "insufficient_logs"),
                    new("symbol", model.Symbol),
                    new("timeframe", model.Timeframe.ToString()));
                _logger.LogDebug(
                    "MLConformalBreakerWorker: skipped model {ModelId} {Symbol}/{Timeframe}; only {Count} usable conformal logs.",
                    model.Id, model.Symbol, model.Timeframe, evaluation.SampleCount);
                continue;
            }

            evaluated++;
            _metrics.MLConformalBreakerModelsEvaluated.Add(1,
                new("symbol", model.Symbol),
                new("timeframe", model.Timeframe.ToString()));
            _metrics.MLConformalBreakerEmpiricalCoverage.Record(
                evaluation.EmpiricalCoverage,
                new("symbol", model.Symbol),
                new("timeframe", model.Timeframe.ToString()));

            if (activeBreaker is not null)
            {
                if (!evaluation.ShouldTrip)
                {
                    recoveryCandidates.Add(new BreakerRecoveryCandidate(
                        activeBreaker.Id,
                        model.Id,
                        model.Symbol,
                        model.Timeframe,
                        evaluation));
                }
                else
                {
                    refreshCandidates.Add(new BreakerRefreshCandidate(
                        activeBreaker.Id,
                        model.Id,
                        model.Symbol,
                        model.Timeframe,
                        evaluation,
                        calibration.CoverageThreshold,
                        calibration.TargetCoverage));
                }

                continue;
            }

            if (!evaluation.ShouldTrip) continue;

            // Compute the suspension window.
            // suspensionBars = 2 × maxRun, capped to prevent a stale breaker from parking a
            // model indefinitely after one pathological run.
            int severityBars = Math.Max(
                evaluation.ConsecutivePoorCoverageBars,
                evaluation.TrippedByCoverageFloor ? triggerRunLength : 0);
            int suspensionBars = Math.Min(Math.Max(severityBars * 2, 1), maxSuspensionBars);

            tripCandidates.Add(new BreakerTripCandidate(
                model.Id,
                model.Symbol,
                model.Timeframe,
                evaluation,
                calibration.CoverageThreshold,
                calibration.TargetCoverage,
                suspensionBars));
        }

        var stateResult = await _stateStore.ApplyAsync(writeDb, tripCandidates, recoveryCandidates, refreshCandidates, ct);
        foreach (var alert in stateResult.Alerts)
        {
            try { await _alertDispatcher.DispatchAsync(alert.Alert, alert.Message, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MLConformalBreakerWorker: alert dispatch failed for {Symbol}; condition={ConditionJson}",
                    alert.Alert.Symbol, alert.Alert.ConditionJson);
            }
        }

        _metrics.MLConformalBreakerActive.Record(stateResult.ActiveBreakers);
        _metrics.WorkerCycleDurationMs.Record(
            Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds,
            new KeyValuePair<string, object?>("worker", nameof(MLConformalBreakerWorker)));

        _logger.LogInformation(
            "MLConformalBreakerWorker: cycle complete. evaluated={Evaluated} skippedNoCalibration={SkippedCalibration} skippedInsufficient={SkippedInsufficient} tripped={Tripped} refreshed={Refreshed} recovered={Recovered} expired={Expired} active={Active}",
            evaluated, skippedNoCalibration, skippedInsufficient, tripCandidates.Count, stateResult.RefreshedCount, stateResult.RecoveredCount, stateResult.ExpiredCount, stateResult.ActiveBreakers);
    }

    private static bool IsUsableCalibration(MLConformalCalibration? calibration, int minLogs)
        => calibration is not null
           && calibration.CalibrationSamples >= minLogs
           && IsFiniteProbability(calibration.TargetCoverage)
           && IsFiniteProbability(calibration.CoverageThreshold);

    private static bool IsFiniteProbability(double value)
        => double.IsFinite(value) && value >= 0.0 && value <= 1.0;

    private static ConformalObservation? TryCreateObservation(
        MLModelPredictionLog log,
        double fallbackThreshold)
    {
        if (log.WasConformalCovered.HasValue)
            return new ConformalObservation(log.WasConformalCovered.Value, log.OutcomeRecordedAt);

        bool? coveredBySet = log.ActualDirection.HasValue
            ? MLFeatureHelper.WasActualDirectionInConformalSet(
                log.ConformalPredictionSetJson,
                log.ActualDirection.Value)
            : null;
        if (coveredBySet.HasValue)
            return new ConformalObservation(coveredBySet.Value, log.OutcomeRecordedAt);

        double score = log.ConformalNonConformityScore
            ?? (log.ActualDirection.HasValue
                ? MLFeatureHelper.ComputeLoggedConformalNonConformityScore(
                    log,
                    log.ActualDirection.Value,
                    fallbackThreshold)
                : double.NaN);
        double threshold = log.ConformalThresholdUsed ?? fallbackThreshold;

        return IsFiniteProbability(score) && IsFiniteProbability(threshold)
            ? new ConformalObservation(score <= threshold, log.OutcomeRecordedAt)
            : null;
    }

    internal static TimeSpan GetBarDuration(Timeframe timeframe) => timeframe switch
    {
        Timeframe.M1  => TimeSpan.FromMinutes(1),
        Timeframe.M5  => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.H1  => TimeSpan.FromHours(1),
        Timeframe.H4  => TimeSpan.FromHours(4),
        Timeframe.D1  => TimeSpan.FromDays(1),
        _             => TimeSpan.FromHours(1),
    };

    private TimeSpan GetInitialDelay() =>
        TimeSpan.FromMinutes(Math.Clamp(_options.InitialDelayMinutes, 0, 24 * 60));

    private TimeSpan GetInterval() =>
        TimeSpan.FromHours(Math.Clamp(_options.PollIntervalHours, 1, 24 * 7));

}
