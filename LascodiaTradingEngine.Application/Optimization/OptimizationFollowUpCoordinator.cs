using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationFollowUpCoordinator
{
    private static readonly TimeSpan FollowUpStuckThreshold = TimeSpan.FromHours(6);
    private static readonly TimeSpan FollowUpInflightRecheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FollowUpRepairRecheckInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly TradingMetrics _metrics;

    public OptimizationFollowUpCoordinator(
        IServiceScopeFactory scopeFactory,
        ILogger<OptimizationFollowUpCoordinator> logger,
        TradingMetrics metrics)
        : this(scopeFactory, (ILogger)logger, metrics)
    {
    }

    internal OptimizationFollowUpCoordinator(
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        TradingMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
    }

    internal async Task MonitorAsync(
        OptimizationConfig config,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var alertDispatcher = scope.ServiceProvider.GetService<IAlertDispatcher>();
        var writeDb = writeCtx.GetDbContext();
        var nowUtc = DateTime.UtcNow;
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
            if (hadStoredSnapshot && !TryGetRunScopedConfigSnapshot(run, out runScopedConfig))
            {
                await FailRunForMalformedConfigSnapshotAsync(
                    writeDb,
                    writeCtx,
                    alertDispatcher,
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
                    alertDispatcher,
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
                    writeDb, writeCtx, alertDispatcher, run, backtestRun, wfRun, ct);
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
                    .FirstOrDefaultAsync(a => a.Symbol == BuildFollowUpFailureAlertSymbol(run.Id) && !a.IsDeleted, ct);

                if (followUpAlert is null)
                {
                    followUpAlert = new Alert();
                    writeDb.Set<Alert>().Add(followUpAlert);
                }

                followUpAlertMessage = PopulateFollowUpFailureAlert(
                    followUpAlert,
                    run.Id,
                    run.StrategyId,
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
                if (alertDispatcher is null)
                {
                    _logger.LogDebug(
                        "Optimization follow-up failure alert for run {RunId} was persisted but no IAlertDispatcher is registered",
                        run.Id);
                    continue;
                }

                try
                {
                    await alertDispatcher.DispatchBySeverityAsync(followUpAlert, followUpAlertMessage, ct);
                }
                catch (Exception ex)
                {
                    OptimizationRunProgressTracker.RecordOperationalIssue(
                        run,
                        "FollowUpAlertDispatchFailed",
                        $"Follow-up failure alert dispatch degraded: {ex.Message}",
                        DateTime.UtcNow);
                    await writeCtx.SaveChangesAsync(CancellationToken.None);
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
            if (!TryGetRunScopedConfigSnapshot(run, out var runScopedConfig))
            {
                throw new OptimizationConfigSnapshotException(run.Id);
            }

            followUpInitialBalance = runScopedConfig.ScreeningInitialBalance;
        }

        bool hasBacktest = existingBacktest is not null;
        if (existingBacktest is null)
        {
            writeDb.Set<BacktestRun>().Add(new BacktestRun
            {
                StrategyId = run.StrategyId,
                Symbol = strategy.Symbol,
                Timeframe = strategy.Timeframe,
                FromDate = fromDate,
                ToDate = toDate,
                InitialBalance = followUpInitialBalance,
                Status = RunStatus.Queued,
                StartedAt = default,
                SourceOptimizationRunId = run.Id,
                ParametersSnapshotJson = followUpParamsJson
            });
        }
        else
        {
            if (existingBacktest.Status == RunStatus.Queued)
            {
                existingBacktest.FromDate = fromDate;
                existingBacktest.ToDate = toDate;
                existingBacktest.InitialBalance = followUpInitialBalance;
                existingBacktest.ParametersSnapshotJson = followUpParamsJson;
            }
            else if (string.IsNullOrWhiteSpace(existingBacktest.ParametersSnapshotJson))
            {
                existingBacktest.ParametersSnapshotJson = followUpParamsJson;
            }
        }

        bool hasWalkForward = existingWalkForward is not null;
        if (existingWalkForward is null)
        {
            writeDb.Set<WalkForwardRun>().Add(new WalkForwardRun
            {
                StrategyId = run.StrategyId,
                Symbol = strategy.Symbol,
                Timeframe = strategy.Timeframe,
                FromDate = fromDate,
                ToDate = toDate,
                InSampleDays = 90,
                OutOfSampleDays = 30,
                InitialBalance = followUpInitialBalance,
                ReOptimizePerFold = false,
                Status = RunStatus.Queued,
                StartedAt = default,
                SourceOptimizationRunId = run.Id,
                ParametersSnapshotJson = followUpParamsJson
            });
        }
        else
        {
            if (existingWalkForward.Status == RunStatus.Queued)
            {
                existingWalkForward.FromDate = fromDate;
                existingWalkForward.ToDate = toDate;
                existingWalkForward.InitialBalance = followUpInitialBalance;
                existingWalkForward.ParametersSnapshotJson = followUpParamsJson;
            }
            else if (string.IsNullOrWhiteSpace(existingWalkForward.ParametersSnapshotJson))
            {
                existingWalkForward.ParametersSnapshotJson = followUpParamsJson;
            }

            existingWalkForward.ReOptimizePerFold = false;
        }

        bool hadAllFollowUpsBeforeRepair = hasBacktest && hasWalkForward;
        var nowUtc = DateTime.UtcNow;
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

    internal static bool TryGetRunScopedConfigSnapshot(
        OptimizationRun run,
        out OptimizationConfig config)
        => OptimizationRunScopedConfigService.TryGetRunScopedConfigSnapshot(run, out config);

    private async Task FailRunForMalformedConfigSnapshotAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        IAlertDispatcher? alertDispatcher,
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
            .FirstOrDefaultAsync(a => a.Symbol == BuildFollowUpFailureAlertSymbol(run.Id) && !a.IsDeleted, ct);

        if (alert is null)
        {
            alert = new Alert();
            writeDb.Set<Alert>().Add(alert);
        }

        string alertMessage = PopulateFollowUpFailureAlert(
            alert,
            run.Id,
            run.StrategyId,
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

        if (alertDispatcher is null)
            return;

        try
        {
            await alertDispatcher.DispatchBySeverityAsync(alert, alertMessage, ct);
        }
        catch (Exception ex)
        {
            OptimizationRunProgressTracker.RecordOperationalIssue(
                run,
                "MalformedSnapshotAlertDispatchFailed",
                $"Malformed-snapshot follow-up alert dispatch degraded: {ex.Message}",
                DateTime.UtcNow);
            await writeCtx.SaveChangesAsync(CancellationToken.None);
            _logger.LogWarning(ex,
                "Optimization follow-up malformed-snapshot alert dispatch failed for run {RunId} (non-fatal)",
                run.Id);
        }
    }

    internal static string BuildFollowUpFailureAlertSymbol(long optimizationRunId)
        => $"OptimizationRun:{optimizationRunId}:FollowUp";

    internal static string PopulateFollowUpFailureAlert(
        Alert alert,
        long optimizationRunId,
        long strategyId,
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
        alert.Symbol = BuildFollowUpFailureAlertSymbol(optimizationRunId);
        alert.Channel = AlertChannel.Webhook;
        alert.Destination = string.Empty;
        alert.Severity = AlertSeverity.High;
        alert.IsActive = true;
        alert.LastTriggeredAt = utcNow;
        alert.DeduplicationKey = alert.Symbol;
        alert.CooldownSeconds = (int)TimeSpan.FromHours(1).TotalSeconds;
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
    {
        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("IX_BacktestRun_SourceOptimizationRunId", StringComparison.OrdinalIgnoreCase)
            || message.Contains("IX_WalkForwardRun_SourceOptimizationRunId", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
               && message.Contains("SourceOptimizationRunId", StringComparison.OrdinalIgnoreCase);
    }

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
            GetIncompleteFollowUpAnchorUtc(run, backtestRun.StartedAt, backtestRun.Status),
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
        IAlertDispatcher? alertDispatcher,
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
                        run.ValidationFollowUpsCreatedAt ??= DateTime.UtcNow;
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
            .FirstOrDefaultAsync(a => a.Symbol == BuildFollowUpFailureAlertSymbol(run.Id) && !a.IsDeleted, ct);

        if (missingFollowUpAlert is null)
        {
            missingFollowUpAlert = new Alert();
            writeDb.Set<Alert>().Add(missingFollowUpAlert);
        }

        string missingFollowUpAlertMessage = PopulateFollowUpFailureAlert(
            missingFollowUpAlert,
            run.Id,
            run.StrategyId,
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

        if (alertDispatcher is not null)
        {
            try
            {
                await alertDispatcher.DispatchBySeverityAsync(missingFollowUpAlert, missingFollowUpAlertMessage, ct);
            }
            catch (Exception ex)
            {
                OptimizationRunProgressTracker.RecordOperationalIssue(
                    run,
                    "MissingFollowUpAlertDispatchFailed",
                    $"Missing follow-up alert dispatch degraded: {ex.Message}",
                    DateTime.UtcNow);
                await writeCtx.SaveChangesAsync(CancellationToken.None);
                _logger.LogWarning(ex,
                    "Missing follow-up alert dispatch failed for run {RunId} (non-fatal)",
                    run.Id);
            }
        }
    }

    private async Task DetectAndAlertOnStuckFollowUpsAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        IAlertDispatcher? alertDispatcher,
        OptimizationRun run,
        BacktestRun backtestRun,
        WalkForwardRun walkForwardRun,
        CancellationToken ct)
    {
        DateTime? backtestAnchorUtc = GetIncompleteFollowUpAnchorUtc(run, backtestRun.StartedAt, backtestRun.Status);
        DateTime? walkForwardAnchorUtc = GetIncompleteFollowUpAnchorUtc(run, walkForwardRun.StartedAt, walkForwardRun.Status);

        double? backtestAgeHours = backtestAnchorUtc.HasValue
            ? (DateTime.UtcNow - backtestAnchorUtc.Value).TotalHours
            : null;
        double? walkForwardAgeHours = walkForwardAnchorUtc.HasValue
            ? (DateTime.UtcNow - walkForwardAnchorUtc.Value).TotalHours
            : null;

        bool backtestStuck = backtestAgeHours.HasValue && backtestAgeHours.Value >= FollowUpStuckThreshold.TotalHours;
        bool walkForwardStuck = walkForwardAgeHours.HasValue && walkForwardAgeHours.Value >= FollowUpStuckThreshold.TotalHours;

        if (!backtestStuck && !walkForwardStuck)
            return;

        string alertSymbol = BuildFollowUpStuckAlertSymbol(run.Id);
        var nowUtc = DateTime.UtcNow;
        var alert = await writeDb.Set<Alert>()
            .FirstOrDefaultAsync(a => a.Symbol == alertSymbol && !a.IsDeleted, ct);

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
        alert.Symbol = alertSymbol;
        alert.Channel = AlertChannel.Webhook;
        alert.Destination = string.Empty;
        alert.Severity = AlertSeverity.High;
        alert.IsActive = true;
        alert.DeduplicationKey = alertSymbol;
        alert.CooldownSeconds = (int)TimeSpan.FromHours(1).TotalSeconds;
        alert.LastTriggeredAt = nowUtc;
        alert.ConditionJson = JsonSerializer.Serialize(new
        {
            Type = "OptimizationFollowUpStuck",
            OptimizationRunId = run.Id,
            StrategyId = run.StrategyId,
            ThresholdHours = FollowUpStuckThreshold.TotalHours,
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

        if (alertDispatcher is null)
            return;

        try
        {
            await alertDispatcher.DispatchBySeverityAsync(alert, message, ct);
        }
            catch (Exception ex)
            {
                OptimizationRunProgressTracker.RecordOperationalIssue(
                    run,
                    "StuckFollowUpAlertDispatchFailed",
                    $"Stuck follow-up alert dispatch degraded: {ex.Message}",
                    DateTime.UtcNow);
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

    private static string BuildFollowUpStuckAlertSymbol(long optimizationRunId)
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
