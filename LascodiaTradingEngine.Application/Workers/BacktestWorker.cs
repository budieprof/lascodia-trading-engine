using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

public class BacktestWorker : BackgroundService
{
    private readonly ILogger<BacktestWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBacktestEngine _backtestEngine;
    private readonly IBacktestRunClaimService _runClaimService;
    private readonly IValidationSettingsProvider _settingsProvider;
    private readonly IBacktestAutoScheduler _autoScheduler;
    private readonly IValidationRunFactory _validationRunFactory;
    private readonly IBacktestOptionsSnapshotBuilder _optionsSnapshotBuilder;
    private readonly IStrategyExecutionSnapshotBuilder _strategySnapshotBuilder;
    private readonly IValidationCandleSeriesGuard _candleSeriesGuard;
    private readonly IAutoWalkForwardWindowPolicy _autoWalkForwardWindowPolicy;
    private readonly IValidationWorkerIdentity _workerIdentity;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private const string AutoScheduleLockKey = "BacktestWorker:AutoSchedule";

    private static readonly TimeSpan ProcessingPollInterval = TimeSpan.FromSeconds(10);
    private DateTime _nextScheduleScanUtc = DateTime.MinValue;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public BacktestWorker(
        ILogger<BacktestWorker> logger,
        IServiceScopeFactory scopeFactory,
        IBacktestEngine backtestEngine,
        IBacktestRunClaimService runClaimService,
        IValidationSettingsProvider settingsProvider,
        IBacktestAutoScheduler autoScheduler,
        IValidationRunFactory validationRunFactory,
        IBacktestOptionsSnapshotBuilder optionsSnapshotBuilder,
        IStrategyExecutionSnapshotBuilder strategySnapshotBuilder,
        IValidationCandleSeriesGuard candleSeriesGuard,
        IAutoWalkForwardWindowPolicy autoWalkForwardWindowPolicy,
        IValidationWorkerIdentity workerIdentity,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _backtestEngine = backtestEngine;
        _runClaimService = runClaimService;
        _settingsProvider = settingsProvider;
        _autoScheduler = autoScheduler;
        _validationRunFactory = validationRunFactory;
        _optionsSnapshotBuilder = optionsSnapshotBuilder;
        _strategySnapshotBuilder = strategySnapshotBuilder;
        _candleSeriesGuard = candleSeriesGuard;
        _autoWalkForwardWindowPolicy = autoWalkForwardWindowPolicy;
        _workerIdentity = workerIdentity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BacktestWorker starting (with auto-scheduling).");
        await using (var startupScope = _scopeFactory.CreateAsyncScope())
        {
            var startupWriteDb = startupScope.ServiceProvider
                .GetRequiredService<IWriteApplicationDbContext>()
                .GetDbContext();
            _runClaimService.EnsureSupportedProvider(startupWriteDb);
        }

        _healthMonitor?.RecordWorkerMetadata(
            nameof(BacktestWorker),
            "Executes queued validation backtests and auto-schedules stale active-strategy refreshes.",
            ProcessingPollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            long cycleStarted = Stopwatch.GetTimestamp();
            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(nameof(BacktestWorker));

                await using var scope = _scopeFactory.CreateAsyncScope();
                var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var writeDb = writeContext.GetDbContext();
                var distributedLock = scope.ServiceProvider.GetService<IDistributedLock>();
                var scheduleSettings = await _settingsProvider.GetBacktestSettingsAsync(writeDb, _logger, stoppingToken);

                if (UtcNow >= _nextScheduleScanUtc)
                {
                    _nextScheduleScanUtc = UtcNow.AddSeconds(scheduleSettings.SchedulePollSeconds);

                    await using var scheduleLock = distributedLock == null
                        ? null
                        : await distributedLock.TryAcquireAsync(
                            AutoScheduleLockKey,
                            TimeSpan.FromSeconds(15),
                            stoppingToken);

                    if (distributedLock != null && scheduleLock == null)
                    {
                        _logger.LogInformation(
                            "BacktestWorker: auto-schedule lock already held elsewhere; skipping this scan");
                    }
                    else
                    {
                        if (scheduleSettings.Enabled)
                            await ScheduleBacktestsForStaleStrategiesCoreAsync(writeDb, scheduleSettings, stoppingToken);

                        await RecoverStaleRunsCoreAsync(writeDb, writeContext, scheduleSettings, stoppingToken);
                    }
                }

                await ProcessNextQueuedRunAsync(stoppingToken);
                _healthMonitor?.RecordCycleSuccess(
                    nameof(BacktestWorker),
                    (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _healthMonitor?.RecordCycleFailure(nameof(BacktestWorker), ex.Message);
                _logger.LogError(ex, "Unexpected error in BacktestWorker polling loop");
            }

            await Task.Delay(ProcessingPollInterval, stoppingToken);
        }

        _healthMonitor?.RecordWorkerStopped(nameof(BacktestWorker));
        _logger.LogInformation("BacktestWorker stopped.");
    }

    private async Task ScheduleBacktestsForStaleStrategiesAsync(
        DbContext writeDb,
        CancellationToken ct)
    {
        var settings = await _settingsProvider.GetBacktestSettingsAsync(writeDb, _logger, ct);
        await _autoScheduler.ScheduleAsync(writeDb, settings, ct);
    }

    private Task ScheduleBacktestsForStaleStrategiesCoreAsync(
        DbContext writeDb,
        BacktestWorkerSettings settings,
        CancellationToken ct)
        => _autoScheduler.ScheduleAsync(writeDb, settings, ct);

    private async Task RecoverStaleRunsAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeContext,
        CancellationToken ct)
    {
        var settings = await _settingsProvider.GetBacktestSettingsAsync(writeDb, _logger, ct);
        await RecoverStaleRunsCoreAsync(writeDb, writeContext, settings, ct);
    }

    private async Task RecoverStaleRunsCoreAsync(
        DbContext writeDb,
        IWriteApplicationDbContext writeContext,
        BacktestWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = UtcNow;
        var (requeued, orphaned) = await _runClaimService.RequeueExpiredRunsAsync(writeDb, nowUtc, ct);

        var legacyCutoff = nowUtc.AddMinutes(-settings.StaleRunMinutes);
        var legacyRunningRuns = await writeDb.Set<BacktestRun>()
            .Where(run => !run.IsDeleted
                       && run.Status == RunStatus.Running
                       && run.ExecutionLeaseExpiresAt == null
                       && (run.LastHeartbeatAt ?? run.ExecutionStartedAt ?? run.ClaimedAt ?? (DateTime?)run.CreatedAt) < legacyCutoff)
            .ToListAsync(ct);

        foreach (var run in legacyRunningRuns)
        {
            BacktestRunStateMachine.Transition(run, RunStatus.Queued, nowUtc);
            _logger.LogWarning(
                "BacktestWorker: re-queued legacy running run {RunId} after stale ownership detection",
                run.Id);
        }

        if (legacyRunningRuns.Count > 0)
            await writeContext.SaveChangesAsync(ct);

        int totalRecovered = requeued + orphaned + legacyRunningRuns.Count;
        if (totalRecovered > 0)
        {
            _healthMonitor?.RecordRecovery(nameof(BacktestWorker), totalRecovered);
            _logger.LogInformation(
                "BacktestWorker: recovered {Count} run(s) ({Requeued} re-queued, {Orphaned} orphaned, {Legacy} legacy)",
                totalRecovered,
                requeued,
                orphaned,
                legacyRunningRuns.Count);
        }
    }

    private async Task ProcessNextQueuedRunAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var eventService = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();
        var priorityResolver = scope.ServiceProvider.GetService<IStrategyValidationPriorityResolver>();

        var db = writeContext.GetDbContext();
        var settings = await _settingsProvider.GetBacktestSettingsAsync(db, _logger, ct);
        int dueBacklogDepth = await db.Set<BacktestRun>()
            .CountAsync(candidate => !candidate.IsDeleted && candidate.Status == RunStatus.Queued && candidate.AvailableAt <= UtcNow, ct);
        _healthMonitor?.RecordBacklogDepth(nameof(BacktestWorker), dueBacklogDepth);
        var claimResult = await _runClaimService.ClaimNextRunAsync(db, UtcNow, _workerIdentity.InstanceId, ct);
        if (!claimResult.RunId.HasValue)
            return;

        var run = await db.Set<BacktestRun>()
            .FirstOrDefaultAsync(candidate => candidate.Id == claimResult.RunId.Value && !candidate.IsDeleted, ct);

        if (run == null)
        {
            _logger.LogWarning("BacktestWorker: claimed run {RunId} disappeared before processing", claimResult.RunId.Value);
            return;
        }

        _logger.LogInformation(
            "BacktestWorker: processing run {RunId} for strategy {StrategyId}",
            run.Id,
            run.StrategyId);
        _healthMonitor?.RecordQueueLatency(
            nameof(BacktestWorker),
            (long)Math.Max(0, (UtcNow - run.QueuedAt).TotalMilliseconds));

        run.ExecutionStartedAt ??= UtcNow;
        run.LastAttemptAt ??= UtcNow;
        run.LastHeartbeatAt = run.ExecutionStartedAt;
        run.ExecutionLeaseExpiresAt = run.ExecutionStartedAt.Value.Add(BacktestExecutionLeasePolicy.LeaseDuration);
        run.ExecutionLeaseToken = claimResult.LeaseToken;
        run.ClaimedByWorkerId ??= _workerIdentity.InstanceId;

        try
        {
            await writeContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(
                ex,
                "BacktestWorker: lease ownership changed before run {RunId} could begin execution",
                run.Id);
            return;
        }

        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var leaseTask = BacktestRunLeaseMaintainer.MaintainExecutionLeaseAsync(
            _scopeFactory,
            _logger,
            run.Id,
            claimResult.LeaseToken,
            leaseCts.Token);

        bool persisted = false;
        bool completed = false;

        try
        {
            var evalStrategy = await ResolveStrategyForExecutionAsync(db, run, ct);

            var candles = await db.Set<Candle>()
                .Where(candle =>
                    candle.Symbol == run.Symbol &&
                    candle.Timeframe == run.Timeframe &&
                    candle.Timestamp >= run.FromDate &&
                    candle.Timestamp <= run.ToDate &&
                    candle.IsClosed &&
                    !candle.IsDeleted)
                .OrderBy(candle => candle.Timestamp)
                .Take(settings.MaxCandlesPerRun + 1)
                .ToListAsync(ct);

            if (candles.Count == 0)
            {
                throw new ValidationRunException(
                    ValidationRunFailureCodes.NoClosedCandles,
                    $"No closed candles found for {run.Symbol}/{run.Timeframe} between {run.FromDate:yyyy-MM-dd} and {run.ToDate:yyyy-MM-dd}.",
                    failureDetailsJson: ValidationRunException.SerializeDetails(new
                    {
                        run.Id,
                        run.Symbol,
                        run.Timeframe,
                        run.FromDate,
                        run.ToDate
                    }));
            }

            if (candles.Count > settings.MaxCandlesPerRun)
            {
                throw new ValidationRunException(
                    ValidationRunFailureCodes.InvalidWindow,
                    $"Candle set exceeds the configured max size for validation ({settings.MaxCandlesPerRun}).",
                    failureDetailsJson: ValidationRunException.SerializeDetails(new
                    {
                        run.Id,
                        CandleCount = candles.Count,
                        settings.MaxCandlesPerRun
                    }));
            }

            var candleAssessment = await _candleSeriesGuard.ValidateAsync(
                db,
                run.Symbol,
                run.Timeframe,
                candles,
                run.FromDate,
                run.ToDate,
                settings.CandleGapMultiplier,
                ct);
            if (!candleAssessment.IsValid)
            {
                throw new ValidationRunException(
                    ValidationRunFailureCodes.InvalidCandleSeries,
                    $"Invalid candle series: {candleAssessment.Issue}.",
                    failureDetailsJson: ValidationRunException.SerializeDetails(new
                    {
                        run.Id,
                        Issue = candleAssessment.Issue
                    }));
            }

            if (!string.IsNullOrWhiteSpace(run.ParametersSnapshotJson))
                evalStrategy.ParametersJson = run.ParametersSnapshotJson;

            if (string.IsNullOrWhiteSpace(run.BacktestOptionsSnapshotJson))
            {
                run.BacktestOptionsSnapshotJson = JsonSerializer.Serialize(
                    await _optionsSnapshotBuilder.BuildAsync(db, run.Symbol, ct));
            }

            var optionsSnapshot = JsonSerializer.Deserialize<BacktestOptionsSnapshot>(run.BacktestOptionsSnapshotJson!)
                ?? throw new JsonException("Backtest options snapshot could not be deserialized.");
            var backtestOptions = optionsSnapshot.ToOptions();
            //--- Inject realised TCA profile so backtest PnL matches live-fill economics.
            //--- Provider returns a conservative default when no TCA samples yet exist, so
            //--- untested symbols still get penalised rather than looking free in backtest.
            var tcaProvider = scope.ServiceProvider.GetService<LascodiaTradingEngine.Application.Services.ITcaCostModelProvider>();
            if (tcaProvider is not null)
                backtestOptions.TcaProfile = await tcaProvider.GetAsync(run.Symbol, ct);

            var result = await _backtestEngine.RunAsync(
                evalStrategy,
                candles,
                run.InitialBalance,
                ct,
                backtestOptions);

            BacktestRunStateMachine.Transition(run, RunStatus.Completed, UtcNow);
            run.ResultJson = JsonSerializer.Serialize(result);
            run.TotalTrades = result.TotalTrades;
            run.WinRate = result.WinRate;
            run.ProfitFactor = result.ProfitFactor;
            run.MaxDrawdownPct = result.MaxDrawdownPct;
            run.SharpeRatio = result.SharpeRatio;
            run.FinalBalance = result.FinalBalance;
            run.TotalReturn = result.TotalReturn;

            _logger.LogInformation(
                "BacktestWorker: run {RunId} completed — TotalTrades={Trades}, WinRate={WinRate:P2}",
                run.Id,
                result.TotalTrades,
                (double)result.WinRate);

            if (!run.SourceOptimizationRunId.HasValue)
            {
                if (_autoWalkForwardWindowPolicy.TryCreateWindow(run, settings, out var windowSelection))
                {
                    string validationQueueKey = priorityResolver?.BuildWalkForwardQueueKey(run)
                        ?? $"backtest:{run.Id}:walk_forward";
                    var walkForwardRun = await _validationRunFactory.BuildWalkForwardRunAsync(
                        db,
                        new WalkForwardQueueRequest(
                            StrategyId: run.StrategyId,
                            Symbol: run.Symbol,
                            Timeframe: run.Timeframe,
                            FromDate: run.FromDate,
                            ToDate: run.ToDate,
                            InSampleDays: windowSelection.InSampleDays,
                            OutOfSampleDays: windowSelection.OutOfSampleDays,
                            InitialBalance: run.InitialBalance,
                            QueueSource: ValidationRunQueueSources.BacktestFollowUp,
                            Priority: run.Priority,
                            ReOptimizePerFold: false,
                            ParametersSnapshotJson: run.ParametersSnapshotJson,
                            StrategySnapshotJson: run.StrategySnapshotJson,
                            ValidationQueueKey: validationQueueKey,
                            BacktestOptionsSnapshotJson: run.BacktestOptionsSnapshotJson),
                        ct);

                    await db.Set<WalkForwardRun>().AddAsync(walkForwardRun, ct);
                }
                else
                {
                    _logger.LogWarning(
                        "BacktestWorker: skipping auto-queue of WalkForwardRun for run {RunId} — {Reason}",
                        run.Id,
                        windowSelection.SkipReason);
                }
            }

            try
            {
                await writeContext.SaveChangesAsync(ct);
                persisted = true;
                completed = true;
            }
            catch (DbUpdateException ex) when (IsActiveValidationQueueViolation(ex, "IX_WalkForwardRun_ActiveValidationQueueKey"))
            {
                foreach (var entry in db.ChangeTracker.Entries<WalkForwardRun>().Where(entry => entry.State == EntityState.Added).ToList())
                    entry.State = EntityState.Detached;

                await writeContext.SaveChangesAsync(ct);
                persisted = true;
                completed = true;
                _logger.LogDebug(
                    ex,
                    "BacktestWorker: concurrent worker already queued walk-forward follow-up for backtest run {RunId}; treating as idempotent success",
                    run.Id);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RequeueCanceledRunAsync(writeContext, run);
            throw;
        }
        catch (Exception ex)
        {
            var failure = ValidationRetryPolicy.Classify(ex);
            var nowUtc = UtcNow;

            if (ValidationRetryPolicy.ShouldRetry(failure, run.RetryCount, settings.MaxRetryAttempts))
            {
                var nextAvailableAtUtc = ValidationRetryPolicy.ComputeNextQueueTimeUtc(nowUtc, run.RetryCount, settings.RetryBackoffSeconds);
                ValidationRetryPolicy.RequeueBacktestRunForRetry(run, nowUtc, nextAvailableAtUtc);
                _healthMonitor?.RecordRetry(nameof(BacktestWorker));
                _logger.LogWarning(
                    ex,
                    "BacktestWorker: run {RunId} hit transient failure {FailureCode}; re-queued for retry #{RetryCount} at {NextQueueTimeUtc:O}",
                    run.Id,
                    failure.FailureCode,
                    run.RetryCount,
                    nextAvailableAtUtc);
            }
            else
            {
                _logger.LogError(ex, "BacktestWorker: run {RunId} failed", run.Id);
                BacktestRunStateMachine.Transition(
                    run,
                    RunStatus.Failed,
                    nowUtc,
                    ex.Message,
                    failure.FailureCode,
                    failure.FailureDetailsJson);
            }

            try
            {
                await writeContext.SaveChangesAsync(ct);
                persisted = true;
            }
            catch (DbUpdateConcurrencyException concurrencyEx)
            {
                _logger.LogWarning(
                    concurrencyEx,
                    "BacktestWorker: lease ownership changed before failure state for run {RunId} could be persisted",
                    run.Id);
            }
        }
        finally
        {
            leaseCts.Cancel();
            try
            {
                await leaseTask;
            }
            catch (OperationCanceledException) when (leaseCts.IsCancellationRequested || ct.IsCancellationRequested)
            {
            }

            if (persisted && run.ExecutionStartedAt.HasValue && run.Status != RunStatus.Running)
            {
                _healthMonitor?.RecordExecutionDuration(
                    nameof(BacktestWorker),
                    (long)Math.Max(0, (UtcNow - run.ExecutionStartedAt.Value).TotalMilliseconds));
            }
        }

        if (persisted && completed)
        {
            await eventService.SaveAndPublish(writeContext, new BacktestCompletedIntegrationEvent
            {
                BacktestRunId = run.Id,
                StrategyId = run.StrategyId,
                Symbol = run.Symbol,
                Timeframe = run.Timeframe,
                FromDate = run.FromDate,
                ToDate = run.ToDate,
                InitialBalance = run.InitialBalance,
                CompletedAt = run.CompletedAt ?? UtcNow
            });
        }

        if (persisted
            && run.SourceOptimizationRunId.HasValue
            && (run.Status == RunStatus.Completed || run.Status == RunStatus.Failed))
        {
            bool followUpPassed = run.Status == RunStatus.Completed;
            if (followUpPassed)
            {
                decimal minHealthScore = await _settingsProvider.GetDecimalAsync(
                    db,
                    _logger,
                    "Optimization:AutoApprovalMinHealthScore",
                    defaultValue: 0.55m,
                    ct: ct,
                    minInclusive: 0m) * 0.80m;
                int minTrades = await _settingsProvider.GetIntAsync(
                    db,
                    _logger,
                    "Optimization:MinCandidateTrades",
                    defaultValue: 10,
                    ct: ct,
                    minInclusive: 1);

                if (!Optimization.OptimizationFollowUpQualityEvaluator.IsBacktestQualitySufficient(
                        run,
                        minHealthScore,
                        minTrades,
                        out string reason))
                {
                    followUpPassed = false;
                    _logger.LogWarning(
                        "BacktestWorker: validation backtest for optimization run {OptimizationRunId} failed quality gate — {Reason}",
                        run.SourceOptimizationRunId.Value,
                        reason);
                }
            }

            await Optimization.OptimizationFollowUpTracker.UpdateStatusAsync(
                db,
                run.SourceOptimizationRunId.Value,
                followUpPassed,
                writeContext,
                ct);
        }
    }

    private async Task RequeueCanceledRunAsync(IWriteApplicationDbContext writeContext, BacktestRun run)
    {
        try
        {
            BacktestRunStateMachine.Transition(run, RunStatus.Queued, UtcNow);
            await writeContext.SaveChangesAsync(CancellationToken.None);
            _logger.LogInformation(
                "BacktestWorker: re-queued run {RunId} after cancellation during shutdown",
                run.Id);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(
                ex,
                "BacktestWorker: lease ownership changed before canceled run {RunId} could be re-queued",
                run.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "BacktestWorker: failed to re-queue canceled run {RunId}; lease recovery will handle it later",
                run.Id);
        }
    }

    private static bool IsActiveValidationQueueViolation(DbUpdateException ex, string indexName)
    {
        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains(indexName, StringComparison.OrdinalIgnoreCase)
               || (message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                   && message.Contains("ValidationQueueKey", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Strategy> ResolveStrategyForExecutionAsync(
        DbContext db,
        BacktestRun run,
        CancellationToken ct)
    {
        var snapshot = _strategySnapshotBuilder.Deserialize(run.StrategySnapshotJson);
        if (snapshot is not null)
            return snapshot.ToStrategy();

        var strategy = await db.Set<Strategy>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == run.StrategyId && !candidate.IsDeleted, ct);
        if (strategy == null)
        {
            throw new ValidationRunException(
                ValidationRunFailureCodes.StrategyNotFound,
                $"Strategy {run.StrategyId} not found.",
                failureDetailsJson: ValidationRunException.SerializeDetails(new
                {
                    run.Id,
                    run.StrategyId
                }));
        }

        run.StrategySnapshotJson = await _strategySnapshotBuilder.BuildSnapshotJsonAsync(
            db,
            run.StrategyId,
            run.ParametersSnapshotJson,
            ct);
        snapshot = _strategySnapshotBuilder.Deserialize(run.StrategySnapshotJson);
        if (snapshot is null)
        {
            throw new ValidationRunException(
                ValidationRunFailureCodes.InvalidStrategySnapshot,
                $"Strategy snapshot for run {run.Id} could not be built or deserialized.",
                failureDetailsJson: ValidationRunException.SerializeDetails(new
                {
                    run.Id,
                    run.StrategyId
                }));
        }

        return snapshot.ToStrategy();
    }
}
