using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Thin hosted-service orchestrator for optimization work.
/// The execution pipeline lives in the extracted optimization services.
/// </summary>
public class OptimizationWorker : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<OptimizationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;
    private readonly OptimizationConfigProvider _configProvider;
    private readonly OptimizationSchedulingCoordinator _schedulingCoordinator;
    private readonly OptimizationChronicFailureEscalator _chronicFailureEscalator;
    private readonly OptimizationWorkerHealthRecorder _healthRecorder;
    private readonly TimeProvider _timeProvider;

    private DateTime _nextScheduleScanUtc = DateTime.MinValue;

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationWorker(
        ILogger<OptimizationWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics,
        OptimizationConfigProvider configProvider,
        OptimizationSchedulingCoordinator schedulingCoordinator,
        OptimizationChronicFailureEscalator chronicFailureEscalator,
        OptimizationWorkerHealthRecorder healthRecorder,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _configProvider = configProvider;
        _schedulingCoordinator = schedulingCoordinator;
        _chronicFailureEscalator = chronicFailureEscalator;
        _healthRecorder = healthRecorder;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OptimizationWorker starting");

        try
        {
            await using var recoveryScope = _scopeFactory.CreateAsyncScope();
            var recoveryCoordinator = recoveryScope.ServiceProvider.GetRequiredService<OptimizationRunRecoveryCoordinator>();
            await recoveryCoordinator.RecoverStaleRunningRunsAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OptimizationWorker: crash recovery check failed (non-fatal)");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RequeueExpiredRunningRunsAsync(stoppingToken);
                await RecoverStaleQueuedRunsAsync(stoppingToken);
                await RetryFailedRunsAsync(stoppingToken);
                var reconciliationSummary = await ReconcileLifecycleStateAsync(stoppingToken);
                await MonitorFollowUpResultsAsync(stoppingToken);

                if (UtcNow >= _nextScheduleScanUtc)
                {
                    await using var schedScope = _scopeFactory.CreateAsyncScope();
                    var readCtx = schedScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                    var writeCtx = schedScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var db = readCtx.GetDbContext();

                    var config = await GetOrLoadConfigAsync(db, stoppingToken);
                    _nextScheduleScanUtc = UtcNow.AddSeconds(config.SchedulePollSeconds);

                    if (config.AutoScheduleEnabled)
                        await AutoScheduleUnderperformersAsync(readCtx, writeCtx, config, stoppingToken);
                }

                await ProcessNextQueuedRunAsync(stoppingToken);
                await RecordHealthAsync(reconciliationSummary, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OptimizationWorker: unexpected error in polling loop");
                _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", "OptimizationWorker"));
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("OptimizationWorker stopped");
    }

    internal async Task ProcessNextQueuedRunAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var runProcessor = scope.ServiceProvider.GetRequiredService<OptimizationRunProcessor>();
        await runProcessor.ProcessNextQueuedRunAsync(ct);
    }

    private async Task RequeueExpiredRunningRunsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var recoveryCoordinator = scope.ServiceProvider.GetRequiredService<OptimizationRunRecoveryCoordinator>();
        await recoveryCoordinator.RequeueExpiredRunningRunsAsync(ct);
    }

    private async Task RecoverStaleQueuedRunsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var recoveryCoordinator = scope.ServiceProvider.GetRequiredService<OptimizationRunRecoveryCoordinator>();
        await recoveryCoordinator.RecoverStaleQueuedRunsAsync(ct);
    }

    private async Task RetryFailedRunsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db = readCtx.GetDbContext();
        var recoveryCoordinator = scope.ServiceProvider.GetRequiredService<OptimizationRunRecoveryCoordinator>();
        var config = await GetOrLoadConfigAsync(db, ct);
        await recoveryCoordinator.RetryFailedRunsAsync(config, ct);
    }

    private async Task<OptimizationRunRecoveryCoordinator.LifecycleReconciliationSummary> ReconcileLifecycleStateAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db = readCtx.GetDbContext();
        var recoveryCoordinator = scope.ServiceProvider.GetRequiredService<OptimizationRunRecoveryCoordinator>();
        var config = await GetOrLoadConfigAsync(db, ct);
        return await recoveryCoordinator.ReconcileLifecycleStateAsync(config, ct);
    }

    private async Task MonitorFollowUpResultsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db = readCtx.GetDbContext();
        var followUpCoordinator = scope.ServiceProvider.GetRequiredService<OptimizationFollowUpCoordinator>();
        var config = await GetOrLoadConfigAsync(db, ct);
        await followUpCoordinator.MonitorAsync(config, ct);
    }

    private async Task AutoScheduleUnderperformersAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        OptimizationConfig config,
        CancellationToken ct)
        => await _schedulingCoordinator.AutoScheduleUnderperformersAsync(readCtx, writeCtx, config, ct);

    private async Task EscalateChronicFailuresAsync(
        DbContext db,
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        IMediator mediator,
        IAlertDispatcher alertDispatcher,
        long strategyId,
        string strategyName,
        int maxConsecutiveFailures,
        int baseCooldownDays,
        CancellationToken ct)
        => await _chronicFailureEscalator.EscalateAsync(
            db,
            writeDb,
            writeCtx,
            mediator,
            alertDispatcher,
            strategyId,
            strategyName,
            maxConsecutiveFailures,
            baseCooldownDays,
            ct);

    internal async Task ApplyApprovalDecisionAsync(
        RunContext ctx,
        CandidateValidationResult vr,
        MarketRegimeEnum? currentRegime,
        DateTime candleLookbackStart,
        BacktestOptions screeningOptions)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var approvalCoordinator = scope.ServiceProvider.GetRequiredService<OptimizationApprovalCoordinator>();
        await approvalCoordinator.ApplyAsync(
            ctx,
            ctx.Config.ToApprovalConfig(),
            vr,
            currentRegime,
            candleLookbackStart,
            screeningOptions);
    }

    internal async Task<DataLoadResult> LoadAndValidateCandlesAsync(
        DbContext db,
        OptimizationRun run,
        Strategy strategy,
        OptimizationConfig config,
        CancellationToken runCt)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dataLoader = scope.ServiceProvider.GetRequiredService<OptimizationDataLoader>();
        return await dataLoader.LoadAsync(db, run, strategy, config.ToDataLoadingConfig(), runCt);
    }

    private async Task<OptimizationConfig> GetOrLoadConfigAsync(DbContext db, CancellationToken ct)
        => await _configProvider.LoadAsync(db, ct);

    private async Task RecordHealthAsync(
        OptimizationRunRecoveryCoordinator.LifecycleReconciliationSummary reconciliationSummary,
        CancellationToken ct)
    {
        var cacheSnapshot = _configProvider.GetCacheSnapshot();
        if (cacheSnapshot.Config is null)
            return;

        await _healthRecorder.RecordAsync(
            cacheSnapshot.Config,
            cacheSnapshot.LastLoadedAtUtc,
            cacheSnapshot.NextRefreshDueAtUtc,
            reconciliationSummary,
            ct);
    }
}
