using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Scoped)]
internal sealed class OptimizationRunPreflightService
{
    private readonly ILogger<OptimizationRunPreflightService> _logger;
    private readonly TradingMetrics _metrics;
    private readonly OptimizationValidator _validator;
    private readonly OptimizationRunScopedConfigService _runScopedConfigService;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationRunPreflightService(
        ILogger<OptimizationRunPreflightService> logger,
        TradingMetrics metrics,
        OptimizationValidator validator,
        OptimizationRunScopedConfigService runScopedConfigService,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _metrics = metrics;
        _validator = validator;
        _runScopedConfigService = runScopedConfigService;
        _timeProvider = timeProvider;
    }

    internal async Task<OptimizationConfig?> PrepareAsync(
        OptimizationRun run,
        DbContext db,
        IWriteApplicationDbContext writeCtx,
        CancellationToken ct)
    {
        var nowUtc = UtcNow;
        var config = await _runScopedConfigService.LoadPreflightConfigurationAsync(run, db, ct);
        WarnOnSuspiciousConfig(config);

        _validator.SetInitialBalance(config.ScreeningInitialBalance);
        _validator.EnableCache();
        EnsureDeterministicSeed(run);
        run.ExecutionStartedAt ??= nowUtc;
        SetRunStage(
            run,
            OptimizationExecutionStage.Preflight,
            "Loading run-scoped configuration and running preflight safety checks.",
            nowUtc);
        await OptimizationExecutionLeasePolicy.HeartbeatRunAsync(run, writeCtx, nowUtc, ct);

        var configIssues = OptimizationConfigValidator.Validate(
            config.AutoApprovalImprovementThreshold, config.AutoApprovalMinHealthScore,
            config.MinBootstrapCILower, config.EmbargoRatio, config.TpeBudget,
            config.TpeInitialSamples, config.MaxParallelBacktests, config.ScreeningTimeoutSeconds,
            config.CorrelationParamThreshold, config.SensitivityPerturbPct,
            config.GpEarlyStopPatience, config.CooldownDays, config.CheckpointEveryN, _logger,
            config.SensitivityDegradationTolerance, config.WalkForwardMinMaxRatio,
            config.CostStressMultiplier,
            config.CpcvNFolds, config.CpcvTestFoldCount,
            config.MinOosCandlesForValidation, config.CircuitBreakerThreshold,
            config.MinCandidateTrades, config.SuccessiveHalvingRungs,
            config.RegimeBlendRatio, config.MinEquityCurveR2, config.MaxTradeTimeConcentration);

        if (configIssues.Count > 0)
        {
            var issueStr = string.Join("; ", configIssues);
            _logger.LogError("OptimizationRunPreflightService: invalid configuration - {Issues}", issueStr);
            if (OptimizationRunStateMachine.CanTransition(run.Status, OptimizationRunStatus.Failed))
            {
                run.FailureCategory = OptimizationFailureCategory.ConfigError;
                OptimizationRunStateMachine.Transition(
                    run,
                    OptimizationRunStatus.Failed,
                    nowUtc,
                    $"Invalid configuration: {issueStr}");
            }

            await writeCtx.SaveChangesAsync(ct);
            return null;
        }

        if (config.SeasonalBlackoutEnabled && OptimizationPolicyHelpers.IsInBlackoutPeriod(config.BlackoutPeriods, nowUtc))
        {
            _logger.LogInformation("OptimizationRunPreflightService: seasonal blackout active - deferring run {RunId}", run.Id);
            _metrics.OptimizationRunsDeferred.Add(1, new KeyValuePair<string, object?>("reason", "seasonal_blackout"));
            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, nowUtc);
            run.DeferredUntilUtc = nowUtc.AddHours(6);
            SetRunStage(run, OptimizationExecutionStage.Queued, "Deferred because a configured seasonal blackout is active.", nowUtc);
            await writeCtx.SaveChangesAsync(ct);
            return null;
        }

        if (config.SuppressDuringDrawdownRecovery && await IsInDrawdownRecoveryAsync(db, ct))
        {
            _logger.LogInformation("OptimizationRunPreflightService: drawdown recovery active - deferring run {RunId}", run.Id);
            _metrics.OptimizationRunsDeferred.Add(1, new KeyValuePair<string, object?>("reason", "drawdown_recovery"));
            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, nowUtc);
            run.DeferredUntilUtc = nowUtc.AddMinutes(30);
            SetRunStage(run, OptimizationExecutionStage.Queued, "Deferred because portfolio drawdown recovery is active.", nowUtc);
            await writeCtx.SaveChangesAsync(ct);
            return null;
        }

        var preflightStrategy = await db.Set<Strategy>()
            .Where(s => s.Id == run.StrategyId && !s.IsDeleted)
            .Select(s => new { s.Symbol, s.Timeframe })
            .FirstOrDefaultAsync(ct);

        if (preflightStrategy is not null)
        {
            int regimeStabilityHours = config.RegimeStabilityHours;
            var recentRegimes = await db.Set<MarketRegimeSnapshot>()
                .Where(s => s.Symbol == preflightStrategy.Symbol
                         && s.Timeframe == preflightStrategy.Timeframe
                         && s.DetectedAt >= nowUtc.AddHours(-regimeStabilityHours)
                         && !s.IsDeleted)
                .Select(s => s.Regime)
                .Distinct()
                .CountAsync(ct);

            if (recentRegimes > 1)
            {
                _logger.LogInformation(
                    "OptimizationRunPreflightService: regime transition detected for {Symbol}/{Tf} in last {Hours}h - deferring run {RunId}",
                    preflightStrategy.Symbol,
                    preflightStrategy.Timeframe,
                    regimeStabilityHours,
                    run.Id);
                _metrics.OptimizationRunsDeferred.Add(1, new KeyValuePair<string, object?>("reason", "regime_transition"));
                OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, nowUtc);
                run.DeferredUntilUtc = nowUtc.AddHours(regimeStabilityHours);
                SetRunStage(run, OptimizationExecutionStage.Queued, "Deferred because the market regime is still transitioning.", nowUtc);
                await writeCtx.SaveChangesAsync(ct);
                return null;
            }
        }

        if (config.RequireEADataAvailability && preflightStrategy is not null)
        {
            var maxHeartbeatAge = TimeSpan.FromSeconds(60);
            bool hasActiveEA = await db.Set<EAInstance>()
                .ActiveAndFreshForSymbol(preflightStrategy.Symbol, maxHeartbeatAge)
                .AnyAsync(ct);

            if (!hasActiveEA)
            {
                _logger.LogInformation(
                    "OptimizationRunPreflightService: no active EA instance for {Symbol} - deferring run {RunId} (DATA_UNAVAILABLE)",
                    preflightStrategy.Symbol,
                    run.Id);
                _metrics.OptimizationRunsDeferred.Add(1, new KeyValuePair<string, object?>("reason", "ea_data_unavailable"));
                OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, nowUtc);
                run.DeferredUntilUtc = nowUtc.AddMinutes(15);
                SetRunStage(run, OptimizationExecutionStage.Queued, "Deferred because no active EA instance is feeding fresh data for the symbol.", nowUtc);
                await writeCtx.SaveChangesAsync(ct);
                return null;
            }
        }

        return await _runScopedConfigService.EnsureRunScopedConfigurationAsync(run, config, db, writeCtx, ct);
    }

    private void WarnOnSuspiciousConfig(OptimizationConfig config)
    {
        if (config.MaxConcurrentRuns <= 0) _logger.LogWarning("OptimizationRunPreflightService: MaxConcurrentRuns={V} must be positive", config.MaxConcurrentRuns);
        if (config.BootstrapIterations < 100) _logger.LogWarning("OptimizationRunPreflightService: BootstrapIterations={V} is very low", config.BootstrapIterations);
        if (config.PermutationIterations < 100) _logger.LogWarning("OptimizationRunPreflightService: PermutationIterations={V} is very low", config.PermutationIterations);
        if (config.CooldownDays < 1) _logger.LogWarning("OptimizationRunPreflightService: CooldownDays={V} must be >= 1", config.CooldownDays);
        if (config.RolloutObservationDays < 1) _logger.LogWarning("OptimizationRunPreflightService: RolloutObservationDays={V} must be >= 1", config.RolloutObservationDays);
        if (config.FollowUpMonitorBatchSize < 1) _logger.LogWarning("OptimizationRunPreflightService: FollowUpMonitorBatchSize={V} must be >= 1", config.FollowUpMonitorBatchSize);
        if (config.MaxOosDegradationPct is <= 0 or > 1) _logger.LogWarning("OptimizationRunPreflightService: MaxOosDegradationPct={V} outside (0,1]", config.MaxOosDegradationPct);
        if (config.TpeBudget < config.TpeInitialSamples) _logger.LogWarning("OptimizationRunPreflightService: TpeBudget ({B}) < TpeInitialSamples ({S}) - search will be purely random", config.TpeBudget, config.TpeInitialSamples);
        if (config.ScreeningTimeoutSeconds <= 0) _logger.LogWarning("OptimizationRunPreflightService: ScreeningTimeoutSeconds={V} must be positive", config.ScreeningTimeoutSeconds);
    }

    private static void EnsureDeterministicSeed(OptimizationRun run)
    {
        if (run.DeterministicSeed != 0)
            return;

        run.DeterministicSeed = HashCode.Combine(run.Id, run.StrategyId, run.QueuedAt == default ? run.StartedAt : run.QueuedAt);
    }

    private static void SetRunStage(
        OptimizationRun run,
        OptimizationExecutionStage stage,
        string? message,
        DateTime utcNow)
        => OptimizationRunProgressTracker.SetStage(run, stage, message, utcNow);

    private static async Task<bool> IsInDrawdownRecoveryAsync(DbContext db, CancellationToken ct)
    {
        var latest = await db.Set<DrawdownSnapshot>()
            .Where(s => !s.IsDeleted)
            .OrderByDescending(s => s.RecordedAt)
            .FirstOrDefaultAsync(ct);

        return latest != null && latest.RecoveryMode != RecoveryMode.Normal;
    }
}
