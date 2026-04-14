using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationFollowUpCoordinator
{
    private static readonly TimeSpan DefaultFollowUpStuckThreshold = TimeSpan.FromHours(6);
    private static readonly TimeSpan FollowUpInflightRecheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FollowUpRepairRecheckInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertDispatcher _alertDispatcher;
    private readonly OptimizationRunScopedConfigService _runScopedConfigService;
    private readonly IValidationRunFactory _validationRunFactory;
    private readonly IBacktestOptionsSnapshotBuilder _optionsSnapshotBuilder;
    private readonly IStrategyExecutionSnapshotBuilder _strategySnapshotBuilder;
    private readonly ILogger _logger;
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationFollowUpCoordinator(
        IServiceScopeFactory scopeFactory,
        IAlertDispatcher alertDispatcher,
        OptimizationRunScopedConfigService runScopedConfigService,
        IValidationRunFactory validationRunFactory,
        IBacktestOptionsSnapshotBuilder optionsSnapshotBuilder,
        IStrategyExecutionSnapshotBuilder strategySnapshotBuilder,
        ILogger<OptimizationFollowUpCoordinator> logger,
        TradingMetrics metrics,
        TimeProvider timeProvider)
        : this(scopeFactory, alertDispatcher, runScopedConfigService, validationRunFactory, optionsSnapshotBuilder, strategySnapshotBuilder, (ILogger)logger, metrics, timeProvider)
    {
    }

    internal OptimizationFollowUpCoordinator(
        IServiceScopeFactory scopeFactory,
        IAlertDispatcher alertDispatcher,
        OptimizationRunScopedConfigService runScopedConfigService,
        IValidationRunFactory validationRunFactory,
        IBacktestOptionsSnapshotBuilder optionsSnapshotBuilder,
        IStrategyExecutionSnapshotBuilder strategySnapshotBuilder,
        ILogger logger,
        TradingMetrics metrics,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _alertDispatcher = alertDispatcher;
        _runScopedConfigService = runScopedConfigService;
        _validationRunFactory = validationRunFactory;
        _optionsSnapshotBuilder = optionsSnapshotBuilder;
        _strategySnapshotBuilder = strategySnapshotBuilder;
        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider;
    }

    internal async Task MonitorAsync(
        OptimizationConfig config,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var writeDb = writeCtx.GetDbContext();
        var nowUtc = UtcNow;
        int alertCooldown = await AlertCooldownDefaults.GetCooldownAsync(
            writeDb, AlertCooldownDefaults.CK_Optimization, AlertCooldownDefaults.Default_Optimization, ct);
        decimal defaultMinBacktestHealthScore = config.AutoApprovalMinHealthScore * 0.80m;
        int defaultMinCandidateTrades = config.MinCandidateTrades;
        decimal defaultMaxWalkForwardCv = (decimal)config.MaxCvCoefficientOfVariation;
        int followUpBatchSize = Math.Max(1, config.FollowUpMonitorBatchSize);

        int candidateWindowSize = Math.Max(followUpBatchSize * 4, followUpBatchSize);
        var candidateRuns = await writeDb.Set<OptimizationRun>()
            .Where(r => r.Status == OptimizationRunStatus.Approved
                     && !r.IsDeleted
                     && (r.ValidationFollowUpStatus == ValidationFollowUpStatus.Pending
                         || r.ValidationFollowUpStatus == null)
                     && (r.NextFollowUpCheckAt == null || r.NextFollowUpCheckAt <= nowUtc))
            .OrderBy(r => r.FollowUpLastCheckedAt ?? DateTime.MinValue)
            .ThenBy(r => r.ValidationFollowUpsCreatedAt ?? r.ApprovedAt ?? r.CompletedAt ?? r.ExecutionStartedAt ?? r.ClaimedAt ?? (DateTime?)r.QueuedAt ?? r.StartedAt)
            .Take(candidateWindowSize)
            .ToListAsync(ct);

        if (candidateRuns.Count == 0)
            return;

        var pendingRunIds = candidateRuns.Select(r => r.Id).ToList();
        var pendingStrategyIds = candidateRuns.Select(r => r.StrategyId).Distinct().ToList();
        var backtestRuns = await writeDb.Set<BacktestRun>()
            .Where(b => b.SourceOptimizationRunId.HasValue
                     && pendingRunIds.Contains(b.SourceOptimizationRunId.Value)
                     && !b.IsDeleted)
            .ToListAsync(ct);
        var walkForwardRuns = await writeDb.Set<WalkForwardRun>()
            .Where(w => w.SourceOptimizationRunId.HasValue
                     && pendingRunIds.Contains(w.SourceOptimizationRunId.Value)
                     && !w.IsDeleted)
            .ToListAsync(ct);
        var strategySet = writeDb.Set<Strategy>();
        var strategyLookup = strategySet is null
            ? new Dictionary<long, Strategy>()
            : await strategySet
                .Where(s => pendingStrategyIds.Contains(s.Id) && !s.IsDeleted)
                .ToDictionaryAsync(s => s.Id, ct);

        var backtestLookup = backtestRuns
            .GroupBy(b => b.SourceOptimizationRunId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var walkForwardLookup = walkForwardRuns
            .GroupBy(w => w.SourceOptimizationRunId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var missingOrReadyRuns = candidateRuns
            .Where(r =>
            {
                backtestLookup.TryGetValue(r.Id, out var backtestRun);
                walkForwardLookup.TryGetValue(r.Id, out var walkForwardRun);

                if (backtestRun is null || walkForwardRun is null)
                    return true;

                bool backtestDone = backtestRun.Status is RunStatus.Completed or RunStatus.Failed;
                bool walkForwardDone = walkForwardRun.Status is RunStatus.Completed or RunStatus.Failed;
                return backtestDone && walkForwardDone;
            })
            .Take(followUpBatchSize)
            .ToList();

        var selectedRuns = missingOrReadyRuns;
        if (selectedRuns.Count < followUpBatchSize)
        {
            var selectedRunIds = selectedRuns.Select(r => r.Id).ToHashSet();
            var remainingInflightRuns = candidateRuns
                .Where(r => !selectedRunIds.Contains(r.Id))
                .Take(followUpBatchSize - selectedRuns.Count)
                .ToList();
            selectedRuns = selectedRuns.Concat(remainingInflightRuns).ToList();
        }

        if (selectedRuns.Count == 0)
            return;

        foreach (var run in selectedRuns)
        {
            var monitorAgeAnchor = run.ValidationFollowUpsCreatedAt
                ?? run.ApprovedAt
                ?? run.CompletedAt
                ?? run.ExecutionStartedAt
                ?? run.ClaimedAt
                ?? (DateTime?)run.QueuedAt
                ?? run.StartedAt;
            _metrics.OptimizationFollowUpQueueAgeMs.Record(
                Math.Max(0, (nowUtc - monitorAgeAnchor).TotalMilliseconds));

            run.FollowUpLastCheckedAt = nowUtc;

            bool hadStoredSnapshot = !string.IsNullOrWhiteSpace(run.ConfigSnapshotJson);
            OptimizationConfig? runScopedConfig = null;
            if (hadStoredSnapshot && !_runScopedConfigService.TryLoadRunScopedConfigSnapshot(run, out runScopedConfig))
            {
                await FailRunForMalformedConfigSnapshotAsync(
                    writeDb,
                    writeCtx,
                    run,
                    nowUtc,
                    ct);
                continue;
            }

            decimal minBacktestHealthScore = defaultMinBacktestHealthScore;
            int minCandidateTrades = defaultMinCandidateTrades;
            decimal maxWalkForwardCv = defaultMaxWalkForwardCv;
            if (runScopedConfig is not null)
            {
                minBacktestHealthScore = runScopedConfig.AutoApprovalMinHealthScore * 0.80m;
                minCandidateTrades = runScopedConfig.MinCandidateTrades;
                maxWalkForwardCv = (decimal)runScopedConfig.MaxCvCoefficientOfVariation;
            }

            backtestLookup.TryGetValue(run.Id, out var backtestRun);
            walkForwardLookup.TryGetValue(run.Id, out var wfRun);

            if (backtestRun is null || wfRun is null)
            {
                await RepairOrFailMissingFollowUpsAsync(
                    writeDb,
                    writeCtx,
                    run,
                    strategyLookup.GetValueOrDefault(run.StrategyId),
                    backtestRun,
                    wfRun,
                    runScopedConfig ?? config,
                    nowUtc,
                    ct);
                continue;
            }

            bool backtestDone = backtestRun.Status is RunStatus.Completed or RunStatus.Failed;
            bool wfDone = wfRun.Status is RunStatus.Completed or RunStatus.Failed;

            if (!backtestDone || !wfDone)
            {
                run.NextFollowUpCheckAt = CalculateIncompleteFollowUpRecheckAt(run, backtestRun, wfRun, nowUtc);
                SetFollowUpState(
                    run,
                    "AwaitingCompletion",
                    $"Awaiting follow-up completion. Backtest={backtestRun.Status}, WalkForward={wfRun.Status}.",
                    nowUtc);
                _metrics.OptimizationFollowUpDeferredChecks.Add(1);
                await DetectAndAlertOnStuckFollowUpsAsync(
                    writeDb, writeCtx, run, backtestRun, wfRun, alertCooldown, ct,
                    config.FollowUpStuckThresholdHours);
                await writeCtx.SaveChangesAsync(ct);
                continue;
            }

            string backtestFailureReason = backtestRun.Status == RunStatus.Completed
                ? "backtest follow-up quality gate failed"
                : $"backtest follow-up status is {backtestRun.Status}";
            string walkForwardFailureReason = wfRun.Status == RunStatus.Completed
                ? "walk-forward follow-up quality gate failed"
                : $"walk-forward follow-up status is {wfRun.Status}";

            bool backtestQualityOk =
                backtestRun.Status == RunStatus.Completed
                && OptimizationFollowUpQualityEvaluator.IsBacktestQualitySufficient(
                    backtestRun, minBacktestHealthScore, minCandidateTrades, out backtestFailureReason);
            bool walkForwardQualityOk =
                wfRun.Status == RunStatus.Completed
                && OptimizationFollowUpQualityEvaluator.IsWalkForwardQualitySufficient(
                    wfRun, maxWalkForwardCv, out walkForwardFailureReason);

            Alert? followUpAlert = null;
            string? followUpAlertMessage = null;
            if (!backtestQualityOk || !walkForwardQualityOk)
            {
                run.ValidationFollowUpStatus = ValidationFollowUpStatus.Failed;
                run.NextFollowUpCheckAt = null;
                SetFollowUpState(
                    run,
                    "QualityGateFailed",
                    $"Follow-up quality failed. Backtest={backtestRun.Status}, WalkForward={wfRun.Status}.",
                    nowUtc);
                _metrics.OptimizationFollowUpFailures.Add(1);

                followUpAlert = await writeDb.Set<Alert>()
                    .FirstOrDefaultAsync(a => a.DeduplicationKey == BuildFollowUpFailureDedupKey(run.Id) && !a.IsDeleted, ct);

                if (followUpAlert is null)
                {
                    followUpAlert = new Alert();
                    writeDb.Set<Alert>().Add(followUpAlert);
                }

                followUpAlertMessage = PopulateFollowUpFailureAlert(
                    followUpAlert,
                    alertCooldown,
                    run.Id,
                    run.StrategyId,
                    strategyLookup.GetValueOrDefault(run.StrategyId)?.Symbol,
                    backtestRun.Status,
                    wfRun.Status,
                    backtestQualityOk,
                    walkForwardQualityOk,
                    backtestQualityOk
                        ? "completed successfully"
                        : backtestFailureReason,
                    walkForwardQualityOk
                        ? "completed successfully"
                        : walkForwardFailureReason,
                    nowUtc);
            }
            else
            {
                run.ValidationFollowUpStatus = ValidationFollowUpStatus.Passed;
                run.NextFollowUpCheckAt = null;
                SetFollowUpState(
                    run,
                    "Passed",
                    "All validation follow-ups completed successfully.",
                    nowUtc);
            }

            await writeCtx.SaveChangesAsync(ct);

            if (followUpAlert is not null && followUpAlertMessage is not null)
            {
                try
                {
                    await _alertDispatcher.DispatchAsync(followUpAlert, followUpAlertMessage, ct);
                }
                catch (Exception ex)
                {
                    OptimizationRunProgressTracker.RecordOperationalIssue(
                        run,
                        "FollowUpAlertDispatchFailed",
                        $"Follow-up failure alert dispatch degraded: {ex.Message}",
                        nowUtc);
                    try
                    {
                        await writeCtx.SaveChangesAsync(CancellationToken.None);
                    }
                    catch (Exception persistEx)
                    {
                        _logger.LogWarning(
                            persistEx,
                            "Optimization follow-up degradation marker persistence failed for run {RunId}",
                            run.Id);
                    }
                    _logger.LogWarning(ex,
                        "Optimization follow-up failure alert dispatch failed for run {RunId} (non-fatal)",
                        run.Id);
                }
            }
        }
    }

    internal async Task<bool> EnsureValidationFollowUpsAsync(
        DbContext writeDb,
        OptimizationRun run,
        Strategy strategy,
        OptimizationConfig config,
        CancellationToken ct)
    {
        var existingBacktest = await writeDb.Set<BacktestRun>()
            .FirstOrDefaultAsync(r => r.SourceOptimizationRunId == run.Id && !r.IsDeleted, ct);
        var existingWalkForward = await writeDb.Set<WalkForwardRun>()
            .FirstOrDefaultAsync(r => r.SourceOptimizationRunId == run.Id && !r.IsDeleted, ct);
        var reusableBacktestWindow = existingBacktest is not null && existingBacktest.Status != RunStatus.Queued
            ? existingBacktest
            : null;
        var reusableWalkForwardWindow = existingWalkForward is not null && existingWalkForward.Status != RunStatus.Queued
            ? existingWalkForward
            : null;
        var (fromDate, toDate) = ResolveFollowUpWindowUtc(run, reusableBacktestWindow, reusableWalkForwardWindow);
        string followUpParamsJson = CanonicalParameterJson.Normalize(
            run.BestParametersJson
            ?? strategy.ParametersJson
            ?? "{}");
        decimal followUpInitialBalance = config.ScreeningInitialBalance;
        if (!string.IsNullOrWhiteSpace(run.ConfigSnapshotJson))
        {
            if (!_runScopedConfigService.TryLoadRunScopedConfigSnapshot(run, out var runScopedConfig))
            {
                throw new OptimizationConfigSnapshotException(run.Id);
            }

            followUpInitialBalance = runScopedConfig.ScreeningInitialBalance;
        }
        var nowUtc = UtcNow;
        string strategySnapshotJson = await _strategySnapshotBuilder.BuildSnapshotJsonAsync(
                writeDb,
                run.StrategyId,
                followUpParamsJson,
                ct)
            ?? JsonSerializer.Serialize(StrategyExecutionSnapshot.FromStrategy(strategy, followUpParamsJson));

        bool hasBacktest = existingBacktest is not null;
        if (existingBacktest is null)
        {
            writeDb.Set<BacktestRun>().Add(await _validationRunFactory.BuildBacktestRunAsync(
                writeDb,
                new BacktestQueueRequest(
                    StrategyId: run.StrategyId,
                    Symbol: strategy.Symbol,
                    Timeframe: strategy.Timeframe,
                    FromDate: fromDate,
                    ToDate: toDate,
                    InitialBalance: followUpInitialBalance,
                    QueueSource: ValidationRunQueueSources.OptimizationFollowUp,
                    SourceOptimizationRunId: run.Id,
                    ParametersSnapshotJson: followUpParamsJson,
                    StrategySnapshotJson: strategySnapshotJson,
                    ValidationQueueKey: $"optimization:{run.Id}:backtest"),
                ct));
        }
        else
        {
            if (existingBacktest.Status == RunStatus.Queued)
            {
                existingBacktest.FromDate = fromDate;
                existingBacktest.ToDate = toDate;
                existingBacktest.InitialBalance = followUpInitialBalance;
                existingBacktest.ParametersSnapshotJson = followUpParamsJson;
                existingBacktest.StrategySnapshotJson = strategySnapshotJson;
                existingBacktest.BacktestOptionsSnapshotJson = JsonSerializer.Serialize(
                    await _optionsSnapshotBuilder.BuildAsync(writeDb, strategy.Symbol, ct));
                existingBacktest.QueueSource = ValidationRunQueueSources.OptimizationFollowUp;
                existingBacktest.QueuedAt = nowUtc;
                existingBacktest.AvailableAt = nowUtc;
                existingBacktest.ClaimedAt = null;
                existingBacktest.ClaimedByWorkerId = null;
                existingBacktest.ExecutionStartedAt = null;
                existingBacktest.LastAttemptAt = null;
                existingBacktest.LastHeartbeatAt = null;
                existingBacktest.ExecutionLeaseExpiresAt = null;
                existingBacktest.ExecutionLeaseToken = null;
                existingBacktest.CompletedAt = null;
                existingBacktest.ErrorMessage = null;
                existingBacktest.FailureCode = null;
                existingBacktest.FailureDetailsJson = null;
                existingBacktest.RetryCount = 0;
            }
            else if (string.IsNullOrWhiteSpace(existingBacktest.ParametersSnapshotJson))
            {
                existingBacktest.ParametersSnapshotJson = followUpParamsJson;
                existingBacktest.StrategySnapshotJson ??= strategySnapshotJson;
            }
        }

        bool hasWalkForward = existingWalkForward is not null;
        if (existingWalkForward is null)
        {
            writeDb.Set<WalkForwardRun>().Add(await _validationRunFactory.BuildWalkForwardRunAsync(
                writeDb,
                new WalkForwardQueueRequest(
                    StrategyId: run.StrategyId,
                    Symbol: strategy.Symbol,
                    Timeframe: strategy.Timeframe,
                    FromDate: fromDate,
                    ToDate: toDate,
                    InSampleDays: 90,
                    OutOfSampleDays: 30,
                    InitialBalance: followUpInitialBalance,
                    QueueSource: ValidationRunQueueSources.OptimizationFollowUp,
                    ReOptimizePerFold: false,
                    SourceOptimizationRunId: run.Id,
                    ParametersSnapshotJson: followUpParamsJson,
                    StrategySnapshotJson: strategySnapshotJson,
                    ValidationQueueKey: $"optimization:{run.Id}:walkforward"),
                ct));
        }
        else
        {
            if (existingWalkForward.Status == RunStatus.Queued)
            {
                existingWalkForward.FromDate = fromDate;
                existingWalkForward.ToDate = toDate;
                existingWalkForward.InitialBalance = followUpInitialBalance;
                existingWalkForward.ParametersSnapshotJson = followUpParamsJson;
                existingWalkForward.StrategySnapshotJson = strategySnapshotJson;
                existingWalkForward.BacktestOptionsSnapshotJson = JsonSerializer.Serialize(
                    await _optionsSnapshotBuilder.BuildAsync(writeDb, strategy.Symbol, ct));
                existingWalkForward.QueueSource = ValidationRunQueueSources.OptimizationFollowUp;
                existingWalkForward.StartedAt = nowUtc;
                existingWalkForward.QueuedAt = nowUtc;
                existingWalkForward.AvailableAt = nowUtc;
                existingWalkForward.ClaimedAt = null;
                existingWalkForward.ClaimedByWorkerId = null;
                existingWalkForward.ExecutionStartedAt = null;
                existingWalkForward.LastAttemptAt = null;
                existingWalkForward.LastHeartbeatAt = null;
                existingWalkForward.ExecutionLeaseExpiresAt = null;
                existingWalkForward.ExecutionLeaseToken = null;
                existingWalkForward.CompletedAt = null;
                existingWalkForward.ErrorMessage = null;
                existingWalkForward.FailureCode = null;
                existingWalkForward.FailureDetailsJson = null;
                existingWalkForward.RetryCount = 0;
            }
            else if (string.IsNullOrWhiteSpace(existingWalkForward.ParametersSnapshotJson))
            {
                existingWalkForward.ParametersSnapshotJson = followUpParamsJson;
                existingWalkForward.StrategySnapshotJson ??= strategySnapshotJson;
            }

            existingWalkForward.ReOptimizePerFold = false;
        }

        bool hadAllFollowUpsBeforeRepair = hasBacktest && hasWalkForward;
        if (!run.ValidationFollowUpsCreatedAt.HasValue || !hadAllFollowUpsBeforeRepair)
            run.ValidationFollowUpsCreatedAt = nowUtc;
        run.ValidationFollowUpStatus = ValidationFollowUpStatus.Pending;
        run.FollowUpLastCheckedAt = null;
        run.NextFollowUpCheckAt = nowUtc;
        SetFollowUpState(
            run,
            hadAllFollowUpsBeforeRepair ? "Pending" : "FollowUpsQueued",
            hadAllFollowUpsBeforeRepair
                ? "Validation follow-ups were already present for this optimization run."
                : "Validation follow-up rows were created or refreshed.",
            nowUtc);

        if (run.ApprovedAt.HasValue)
        {
            _metrics.OptimizationApprovalToFollowUpCreationMs.Record(
                Math.Max(0, (nowUtc - run.ApprovedAt.Value).TotalMilliseconds));
        }

        return hadAllFollowUpsBeforeRepair;
    }

    private async Task FailRunForMalformedConfigSnapshotAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        OptimizationRun run,
        DateTime nowUtc,
        CancellationToken ct)
    {
        run.ValidationFollowUpStatus = ValidationFollowUpStatus.Failed;
        run.NextFollowUpCheckAt = null;
        SetFollowUpState(
            run,
            "ConfigSnapshotInvalid",
            "Validation follow-up monitoring could not load the stored run-scoped optimization config snapshot.",
            nowUtc);
        _metrics.OptimizationFollowUpFailures.Add(1);

        var alert = await writeDb.Set<Alert>()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == BuildFollowUpFailureDedupKey(run.Id) && !a.IsDeleted, ct);

        if (alert is null)
        {
            alert = new Alert();
            writeDb.Set<Alert>().Add(alert);
        }

        int alertCooldown = await AlertCooldownDefaults.GetCooldownAsync(
            writeDb, AlertCooldownDefaults.CK_Optimization, AlertCooldownDefaults.Default_Optimization, ct);
        string? strategySymbol = await writeDb.Set<Strategy>()
            .Where(s => s.Id == run.StrategyId && !s.IsDeleted)
            .Select(s => s.Symbol)
            .FirstOrDefaultAsync(ct);
        string alertMessage = PopulateFollowUpFailureAlert(
            alert,
            alertCooldown,
            run.Id,
            run.StrategyId,
            strategySymbol,
            RunStatus.Failed,
            RunStatus.Failed,
            backtestQualityOk: false,
            walkForwardQualityOk: false,
            backtestReason: "stored optimization config snapshot is malformed or unsupported",
            walkForwardReason: "stored optimization config snapshot is malformed or unsupported",
            utcNow: nowUtc);

        await writeCtx.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Optimization follow-up monitoring failed closed for run {RunId} — stored config snapshot is malformed or unsupported",
            run.Id);

        try
        {
            await _alertDispatcher.DispatchAsync(alert, alertMessage, ct);
        }
        catch (Exception ex)
        {
                OptimizationRunProgressTracker.RecordOperationalIssue(
                    run,
                    "MalformedSnapshotAlertDispatchFailed",
                    $"Malformed-snapshot follow-up alert dispatch degraded: {ex.Message}",
                    nowUtc);
            await writeCtx.SaveChangesAsync(CancellationToken.None);
            _logger.LogWarning(ex,
                "Optimization follow-up malformed-snapshot alert dispatch failed for run {RunId} (non-fatal)",
                run.Id);
        }
    }

    internal static string BuildFollowUpFailureDedupKey(long optimizationRunId)
        => $"OptimizationRun:{optimizationRunId}:FollowUp";

    internal static string PopulateFollowUpFailureAlert(
        Alert alert,
        int alertCooldown,
        long optimizationRunId,
        long strategyId,
        string? strategySymbol,
        RunStatus backtestStatus,
        RunStatus walkForwardStatus,
        bool backtestQualityOk,
        bool walkForwardQualityOk,
        string backtestReason,
        string walkForwardReason,
        DateTime utcNow)
    {
        string message =
            $"Optimization follow-up validation failed for run {optimizationRunId} (strategy {strategyId}). " +
            $"Backtest={backtestStatus}, WalkForward={walkForwardStatus}, " +
            $"BacktestReason={backtestReason}, WalkForwardReason={walkForwardReason}.";

        alert.AlertType = AlertType.OptimizationLifecycleIssue;
        alert.Symbol = strategySymbol;
        alert.Severity = AlertSeverity.High;
        alert.IsActive = true;
        alert.LastTriggeredAt = utcNow;
        alert.DeduplicationKey = BuildFollowUpFailureDedupKey(optimizationRunId);
        alert.CooldownSeconds = alertCooldown;
        alert.ConditionJson = JsonSerializer.Serialize(new
        {
            Type = "OptimizationFollowUpFailure",
            OptimizationRunId = optimizationRunId,
            StrategyId = strategyId,
            BacktestStatus = backtestStatus.ToString(),
            WalkForwardStatus = walkForwardStatus.ToString(),
            BacktestQualityOk = backtestQualityOk,
            WalkForwardQualityOk = walkForwardQualityOk,
            BacktestReason = backtestReason,
            WalkForwardReason = walkForwardReason,
            Message = message,
        });

        return message;
    }

    internal static bool IsDuplicateFollowUpConstraintViolation(DbUpdateException ex)
        => OptimizationDbExceptionClassifier.IsDuplicateFollowUpConstraintViolation(ex);

    internal static void DetachPendingValidationFollowUps(DbContext writeDb, long optimizationRunId)
    {
        foreach (var entry in writeDb.ChangeTracker.Entries<BacktestRun>()
                     .Where(e => e.State == EntityState.Added && e.Entity.SourceOptimizationRunId == optimizationRunId)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var entry in writeDb.ChangeTracker.Entries<WalkForwardRun>()
                     .Where(e => e.State == EntityState.Added && e.Entity.SourceOptimizationRunId == optimizationRunId)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    internal static DateTime CalculateFollowUpRepairRecheckAt(OptimizationRun run, DateTime nowUtc)
    {
        int normalizedAttempts = Math.Max(1, run.FollowUpRepairAttempts);
        int backoffMultiplier = Math.Min(normalizedAttempts - 1, 3);
        return nowUtc.AddMinutes(FollowUpRepairRecheckInterval.TotalMinutes * Math.Pow(2, backoffMultiplier));
    }

    internal static DateTime CalculateIncompleteFollowUpRecheckAt(
        OptimizationRun run,
        BacktestRun backtestRun,
        WalkForwardRun walkForwardRun,
        DateTime nowUtc)
    {
        var anchors = new[]
        {
            GetIncompleteFollowUpAnchorUtc(run, backtestRun.CreatedAt, backtestRun.Status),
            GetIncompleteFollowUpAnchorUtc(run, walkForwardRun.StartedAt, walkForwardRun.Status),
        }.Where(x => x.HasValue).Select(x => x!.Value).ToList();

        if (anchors.Count == 0)
            return nowUtc.Add(FollowUpInflightRecheckInterval);

        double oldestAgeHours = anchors.Max(anchor => Math.Max(0, (nowUtc - anchor).TotalHours));
        TimeSpan interval = oldestAgeHours switch
        {
            < 1 => TimeSpan.FromMinutes(5),
            < 6 => TimeSpan.FromMinutes(10),
            < 24 => FollowUpInflightRecheckInterval,
            _ => TimeSpan.FromMinutes(30),
        };

        return nowUtc.Add(interval);
    }

    private async Task RepairOrFailMissingFollowUpsAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        OptimizationRun run,
        Strategy? strategy,
        BacktestRun? backtestRun,
        WalkForwardRun? wfRun,
        OptimizationConfig repairConfig,
        DateTime nowUtc,
        CancellationToken ct)
    {
        run.FollowUpRepairAttempts++;

        if (strategy is not null)
        {
            bool alreadyComplete = await EnsureValidationFollowUpsAsync(writeDb, run, strategy, repairConfig, ct);
            if (!alreadyComplete)
            {
                run.NextFollowUpCheckAt = CalculateFollowUpRepairRecheckAt(run, nowUtc);
                SetFollowUpState(
                    run,
                    "MissingFollowUps",
                    "Missing validation follow-up rows were repaired and re-queued.",
                    nowUtc);
                _metrics.OptimizationFollowUpRepairs.Add(1);
                _metrics.OptimizationFollowUpDeferredChecks.Add(1);
                try
                {
                    await writeCtx.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (IsDuplicateFollowUpConstraintViolation(ex))
                {
                    DetachPendingValidationFollowUps(writeDb, run.Id);

                    // Verify how many follow-ups actually exist before marking as created.
                    // A partial insertion could have created some but not all.
                    int existingCount = await writeDb.Set<BacktestRun>()
                        .CountAsync(r => r.SourceOptimizationRunId == run.Id && !r.IsDeleted, ct)
                        + await writeDb.Set<WalkForwardRun>()
                            .CountAsync(r => r.SourceOptimizationRunId == run.Id && !r.IsDeleted, ct);

                    if (existingCount > 0)
                    {
                        run.ValidationFollowUpsCreatedAt ??= nowUtc;
                        run.ValidationFollowUpStatus ??= ValidationFollowUpStatus.Pending;
                    }

                    run.NextFollowUpCheckAt = CalculateFollowUpRepairRecheckAt(run, nowUtc);
                    SetFollowUpState(
                        run,
                        "Pending",
                        $"Duplicate follow-up constraint hit; {existingCount} follow-up(s) verified in DB.",
                        nowUtc);
                    await writeCtx.SaveChangesAsync(ct);
                    _metrics.OptimizationDuplicateFollowUpsPrevented.Add(1);
                    _metrics.OptimizationFollowUpRepairs.Add(1);
                    _metrics.OptimizationFollowUpDeferredChecks.Add(1);
                }

                _logger.LogWarning(
                    "Optimization follow-up rows repaired for approved run {RunId} — backtest={HasBacktest}, walk-forward={HasWalkForward}",
                    run.Id, backtestRun is not null, wfRun is not null);
                return;
            }
        }

        run.ValidationFollowUpStatus = ValidationFollowUpStatus.Failed;
        run.NextFollowUpCheckAt = null;
        SetFollowUpState(
            run,
            "MissingFollowUps",
            "Validation follow-up rows are missing and the strategy could not be loaded for repair.",
            nowUtc);
        _metrics.OptimizationFollowUpFailures.Add(1);

        var missingFollowUpAlert = await writeDb.Set<Alert>()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == BuildFollowUpFailureDedupKey(run.Id) && !a.IsDeleted, ct);

        if (missingFollowUpAlert is null)
        {
            missingFollowUpAlert = new Alert();
            writeDb.Set<Alert>().Add(missingFollowUpAlert);
        }

        int alertCooldown = await AlertCooldownDefaults.GetCooldownAsync(
            writeDb, AlertCooldownDefaults.CK_Optimization, AlertCooldownDefaults.Default_Optimization, ct);
        string missingFollowUpAlertMessage = PopulateFollowUpFailureAlert(
            missingFollowUpAlert,
            alertCooldown,
            run.Id,
            run.StrategyId,
            strategy?.Symbol,
            backtestRun?.Status ?? RunStatus.Failed,
            wfRun?.Status ?? RunStatus.Failed,
            backtestQualityOk: false,
            walkForwardQualityOk: false,
            backtestReason: backtestRun is null
                ? "backtest follow-up row missing and strategy unavailable for repair"
                : "backtest follow-up row exists but repair context is unavailable",
            walkForwardReason: wfRun is null
                ? "walk-forward follow-up row missing and strategy unavailable for repair"
                : "walk-forward follow-up row exists but repair context is unavailable",
            utcNow: nowUtc);

        await writeCtx.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Optimization follow-up rows missing for approved run {RunId} and strategy {StrategyId} cannot be loaded for repair",
            run.Id, run.StrategyId);

        try
        {
            await _alertDispatcher.DispatchAsync(missingFollowUpAlert, missingFollowUpAlertMessage, ct);
        }
        catch (Exception ex)
        {
            OptimizationRunProgressTracker.RecordOperationalIssue(
                run,
                "MissingFollowUpAlertDispatchFailed",
                $"Missing follow-up alert dispatch degraded: {ex.Message}",
                nowUtc);
            await writeCtx.SaveChangesAsync(CancellationToken.None);
            _logger.LogWarning(ex,
                "Missing follow-up alert dispatch failed for run {RunId} (non-fatal)",
                run.Id);
        }
    }

    private async Task DetectAndAlertOnStuckFollowUpsAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        OptimizationRun run,
        BacktestRun backtestRun,
        WalkForwardRun walkForwardRun,
        int alertCooldown,
        CancellationToken ct,
        double followUpStuckThresholdHours = 0)
    {
        DateTime? backtestAnchorUtc = GetIncompleteFollowUpAnchorUtc(run, backtestRun.CreatedAt, backtestRun.Status);
        DateTime? walkForwardAnchorUtc = GetIncompleteFollowUpAnchorUtc(run, walkForwardRun.StartedAt, walkForwardRun.Status);

        var nowUtc = UtcNow;
        double? backtestAgeHours = backtestAnchorUtc.HasValue
            ? (nowUtc - backtestAnchorUtc.Value).TotalHours
            : null;
        double? walkForwardAgeHours = walkForwardAnchorUtc.HasValue
            ? (nowUtc - walkForwardAnchorUtc.Value).TotalHours
            : null;

        double effectiveStuckThresholdHours = followUpStuckThresholdHours > 0
            ? followUpStuckThresholdHours
            : DefaultFollowUpStuckThreshold.TotalHours;
        bool backtestStuck = backtestAgeHours.HasValue && backtestAgeHours.Value >= effectiveStuckThresholdHours;
        bool walkForwardStuck = walkForwardAgeHours.HasValue && walkForwardAgeHours.Value >= effectiveStuckThresholdHours;

        if (!backtestStuck && !walkForwardStuck)
            return;

        string dedupKey = BuildFollowUpStuckDedupKey(run.Id);
        var alert = await writeDb.Set<Alert>()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == dedupKey && !a.IsDeleted, ct);

        bool suppressDispatch = alert?.LastTriggeredAt is DateTime lastTriggeredAt
            && lastTriggeredAt >= nowUtc.AddHours(-1);
        if (suppressDispatch)
            return;

        string message =
            $"Optimization follow-up appears stuck for run {run.Id} (strategy {run.StrategyId}). " +
            $"Backtest={backtestRun.Status} age={FormatFollowUpAge(backtestAgeHours)}, " +
            $"WalkForward={walkForwardRun.Status} age={FormatFollowUpAge(walkForwardAgeHours)}.";

        bool isNewAlert = alert is null;
        alert ??= new Alert();
        alert.AlertType = AlertType.OptimizationLifecycleIssue;
        alert.Severity = AlertSeverity.High;
        alert.IsActive = true;
        alert.DeduplicationKey = dedupKey;
        alert.CooldownSeconds = alertCooldown;
        alert.LastTriggeredAt = nowUtc;
        alert.ConditionJson = JsonSerializer.Serialize(new
        {
            Type = "OptimizationFollowUpStuck",
            OptimizationRunId = run.Id,
            StrategyId = run.StrategyId,
            ThresholdHours = effectiveStuckThresholdHours,
            BacktestStatus = backtestRun.Status.ToString(),
            WalkForwardStatus = walkForwardRun.Status.ToString(),
            BacktestAgeHours = backtestAgeHours,
            WalkForwardAgeHours = walkForwardAgeHours,
            Message = message,
        });

        if (isNewAlert)
            writeDb.Set<Alert>().Add(alert);

        SetFollowUpState(
            run,
            "Stuck",
            $"Follow-up monitoring detected a stuck run. BacktestAge={FormatFollowUpAge(backtestAgeHours)}, WalkForwardAge={FormatFollowUpAge(walkForwardAgeHours)}.",
            nowUtc);

        await writeCtx.SaveChangesAsync(ct);

        try
        {
            await _alertDispatcher.DispatchAsync(alert, message, ct);
        }
        catch (Exception ex)
        {
            OptimizationRunProgressTracker.RecordOperationalIssue(
                run,
                "StuckFollowUpAlertDispatchFailed",
                $"Stuck follow-up alert dispatch degraded: {ex.Message}",
                nowUtc);
            await writeCtx.SaveChangesAsync(CancellationToken.None);
            _logger.LogWarning(ex,
                "Stuck follow-up alert dispatch failed for run {RunId} (non-fatal)",
                run.Id);
        }
    }

    private static (DateTime FromDate, DateTime ToDate) ResolveFollowUpWindowUtc(
        OptimizationRun run,
        BacktestRun? existingBacktest,
        WalkForwardRun? existingWalkForward)
    {
        if (existingBacktest is not null)
            return NormalizeFollowUpWindow(existingBacktest.FromDate, existingBacktest.ToDate, run);

        if (existingWalkForward is not null)
            return NormalizeFollowUpWindow(existingWalkForward.FromDate, existingWalkForward.ToDate, run);

        var anchorUtc = run.ApprovedAt
            ?? run.CompletedAt
            ?? run.ExecutionStartedAt
            ?? run.ClaimedAt
            ?? (DateTime?)run.QueuedAt
            ?? run.StartedAt;
        return (anchorUtc.AddYears(-1), anchorUtc);
    }

    private static (DateTime FromDate, DateTime ToDate) NormalizeFollowUpWindow(
        DateTime fromDate,
        DateTime toDate,
        OptimizationRun run)
    {
        if (toDate <= fromDate)
        {
            var anchorUtc = run.ApprovedAt
                ?? run.CompletedAt
                ?? run.ExecutionStartedAt
                ?? run.ClaimedAt
                ?? (DateTime?)run.QueuedAt
                ?? run.StartedAt;
            return (anchorUtc.AddYears(-1), anchorUtc);
        }

        return (fromDate, toDate);
    }

    private static DateTime? GetIncompleteFollowUpAnchorUtc(
        OptimizationRun run,
        DateTime startedAt,
        RunStatus status)
    {
        if (status is not RunStatus.Queued and not RunStatus.Running)
            return null;

        return status == RunStatus.Running
            ? startedAt
            : run.ValidationFollowUpsCreatedAt
                ?? run.ApprovedAt
                ?? run.CompletedAt
                ?? run.ExecutionStartedAt
                ?? run.ClaimedAt
                ?? (DateTime?)run.QueuedAt
                ?? run.StartedAt;
    }

    private static string BuildFollowUpStuckDedupKey(long optimizationRunId)
        => $"OptimizationRun:{optimizationRunId}:FollowUp:Stuck";

    private static string FormatFollowUpAge(double? ageHours)
        => ageHours.HasValue ? $"{ageHours.Value:F1}h" : "n/a";

    private static void SetFollowUpState(
        OptimizationRun run,
        string statusCode,
        string message,
        DateTime utcNow)
    {
        OptimizationRunProgressTracker.SetStage(run, OptimizationExecutionStage.FollowUp, message, utcNow);
        run.FollowUpLastStatusCode = statusCode;
        run.FollowUpLastStatusMessage = Truncate(message, 500);
        run.FollowUpStatusUpdatedAt = utcNow;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            return value;

        return value[..maxLength];
    }
}
