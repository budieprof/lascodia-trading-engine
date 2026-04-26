using System.Data;
using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

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
/// <item><description><see cref="MLModel.PpcSurprised"/> when posterior predictive surprise rollback is enabled (default on).</description></item>
/// <item><description><see cref="MLModel.LatestOosMaxDrawdown"/> &gt; configured threshold (default 0.30).</description></item>
/// </list>
///
/// <para>
/// Rollback is skipped (logged but not actioned) when no <see cref="MLModel.PreviousChampionModelId"/>
/// exists, or when the previous champion is unsafe for live inference (wrong symbol/timeframe,
/// failed, suppressed, missing model bytes, or itself degraded). Operators must manually
/// intervene in those cases.
/// </para>
/// </summary>
public sealed class MLModelAutoRollbackWorker : BackgroundService
{
    private readonly ILogger<MLModelAutoRollbackWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedLock? _distributedLock;
    private readonly TimeProvider _timeProvider;

    private const string WorkerName = nameof(MLModelAutoRollbackWorker);
    private const string CycleLockKey = "ml-model-auto-rollback:cycle";

    private const string CK_Enabled                  = "MLAutoRollback:Enabled";
    private const string CK_PollSeconds              = "MLAutoRollback:PollIntervalSeconds";
    private const string CK_MaxRetrainFailures        = "MLAutoRollback:MaxConsecutiveRetrainFailures";
    private const string CK_MaxCalibrationDrift       = "MLAutoRollback:MaxPlattCalibrationDrift";
    private const string CK_MinLiveDirectionAccuracy  = "MLAutoRollback:MinLiveDirectionAccuracy";
    private const string CK_MinLivePredictions        = "MLAutoRollback:MinLivePredictionsForAccuracyCheck";
    private const string CK_RollbackOnPpcSurprise     = "MLAutoRollback:RollbackOnPosteriorPredictiveSurprise";
    private const string CK_MaxOosDrawdown            = "MLAutoRollback:MaxOosDrawdown";
    private const string CK_LockTimeoutSeconds        = "MLAutoRollback:LockTimeoutSeconds";

    private const int     DefaultPollSeconds              = 300;     // 5 min
    private const int     DefaultMaxRetrainFailures        = 3;
    private const double  DefaultMaxCalibrationDrift       = 0.30;
    private const decimal DefaultMinLiveDirectionAccuracy  = 0.45m;
    private const int     DefaultMinLivePredictions        = 50;
    private const bool    DefaultRollbackOnPpcSurprise     = true;
    private const double  DefaultMaxOosDrawdown            = 0.30;
    private const int     DefaultLockTimeoutSeconds        = 5;

    public MLModelAutoRollbackWorker(
        ILogger<MLModelAutoRollbackWorker> logger,
        IServiceScopeFactory scopeFactory,
        IDistributedLock? distributedLock = null,
        TimeProvider? timeProvider = null)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _distributedLock = distributedLock;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
                var result = await RunCycleAsync(stoppingToken);
                pollSecs = result.PollSeconds;
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

    internal async Task<AutoRollbackCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db       = writeCtx.GetDbContext();

        var config = await LoadConfigAsync(db, ct);
        if (!config.Enabled)
        {
            return AutoRollbackCycleResult.Skipped(config.PollSeconds, "disabled");
        }

        await using var cycleLease = await TryAcquireCycleLockAsync(config, ct);
        if (_distributedLock is not null && cycleLease is null)
        {
            _logger.LogDebug("MLModelAutoRollback: skipped because another instance holds {LockKey}", CycleLockKey);
            return AutoRollbackCycleResult.Skipped(config.PollSeconds, "lock_busy");
        }

        var degraded = await db.Set<MLModel>()
            .Where(m => m.IsActive
                     && !m.IsDeleted
                     && m.Status != MLModelStatus.Failed
                     && (m.ConsecutiveRetrainFailures >= config.MaxRetrainFailures
                         || (m.PlattCalibrationDrift != null && m.PlattCalibrationDrift > config.MaxCalibrationDrift)
                         || (m.LiveDirectionAccuracy != null
                             && m.LiveTotalPredictions >= config.MinLivePredictions
                             && m.LiveDirectionAccuracy < config.MinLiveDirectionAccuracy)
                         || (config.RollbackOnPpcSurprise && m.PpcSurprised)
                         || (m.LatestOosMaxDrawdown != null && m.LatestOosMaxDrawdown > config.MaxOosDrawdown)))
            .OrderBy(m => m.Symbol)
            .ThenBy(m => m.Timeframe)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

        var orphans = degraded
            .Where(m => m.PreviousChampionModelId is null)
            .ToList();
        foreach (var orphan in orphans)
        {
            _logger.LogWarning(
                "MLModelAutoRollback: degraded model {Id} ({Symbol}/{Timeframe}) has no PreviousChampionModelId; manual intervention required",
                orphan.Id, orphan.Symbol, orphan.Timeframe);
        }

        var rollbackCandidates = degraded
            .Where(m => m.PreviousChampionModelId is not null)
            .ToList();

        int rollbackCount = 0;
        int skippedRollbackCount = orphans.Count;
        foreach (var candidate in rollbackCandidates)
        {
            if (ct.IsCancellationRequested) break;

            var rollback = await TryRollbackAsync(writeCtx, db, candidate.Id, config, ct);
            if (rollback is null)
            {
                skippedRollbackCount++;
                continue;
            }

            rollbackCount++;
            _logger.LogWarning(
                "MLModelAutoRollback: rolled back model {FailingId} to champion {ChampionId} ({Symbol}/{Timeframe}). Reason: {Reason}",
                rollback.FailingModelId, rollback.FallbackModelId, rollback.Symbol, rollback.Timeframe, rollback.Reason);

            await SafeLogDecisionAsync(mediator, rollback, ct);
        }

        if (rollbackCount > 0)
        {
            _logger.LogInformation(
                "MLModelAutoRollback: cycle complete; rolled back {RollbackCount}/{CandidateCount} degraded candidate model(s)",
                rollbackCount, rollbackCandidates.Count);
        }

        return new AutoRollbackCycleResult(
            PollSeconds: config.PollSeconds,
            DegradedModelCount: degraded.Count,
            RollbackCandidateCount: rollbackCandidates.Count,
            OrphanModelCount: orphans.Count,
            RollbackCount: rollbackCount,
            SkippedRollbackCount: skippedRollbackCount,
            SkippedReason: null);
    }

    private async Task<RollbackAudit?> TryRollbackAsync(
        IWriteApplicationDbContext writeCtx,
        DbContext db,
        long failingModelId,
        AutoRollbackRuntimeConfig config,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var failing = await db.Set<MLModel>()
            .FirstOrDefaultAsync(m => m.Id == failingModelId && !m.IsDeleted, ct);
        if (failing is null || !failing.IsActive || failing.Status == MLModelStatus.Failed)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogDebug(
                "MLModelAutoRollback: model {ModelId} was already actioned before rollback could start",
                failingModelId);
            return null;
        }

        if (!IsDegraded(failing, config))
        {
            await transaction.RollbackAsync(ct);
            _logger.LogDebug(
                "MLModelAutoRollback: model {ModelId} no longer breaches rollback thresholds",
                failing.Id);
            return null;
        }

        if (failing.PreviousChampionModelId is null)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogWarning(
                "MLModelAutoRollback: degraded model {ModelId} ({Symbol}/{Timeframe}) has no fallback champion",
                failing.Id, failing.Symbol, failing.Timeframe);
            return null;
        }

        var fallback = await db.Set<MLModel>()
            .FirstOrDefaultAsync(m => m.Id == failing.PreviousChampionModelId.Value && !m.IsDeleted, ct);
        var ineligibleReason = GetFallbackIneligibilityReason(failing, fallback, config);
        if (ineligibleReason is not null)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogWarning(
                "MLModelAutoRollback: cannot roll back model {FailingId} to PreviousChampionModelId={FallbackId}; {Reason}",
                failing.Id, failing.PreviousChampionModelId, ineligibleReason);
            return null;
        }

        fallback = fallback ?? throw new InvalidOperationException("Fallback was validated as present but resolved to null.");

        var reason = BuildRollbackReason(failing, config);
        var previousFailingStatus = failing.Status;
        var previousFallbackStatus = fallback.Status;

        var activePairModels = await db.Set<MLModel>()
            .Where(m => m.Symbol == failing.Symbol
                     && m.Timeframe == failing.Timeframe
                     && m.IsActive
                     && !m.IsDeleted
                     && m.Id != fallback.Id)
            .ToListAsync(ct);

        foreach (var model in activePairModels)
        {
            var priorStatus = model.Status;
            model.IsActive = false;
            model.IsFallbackChampion = false;

            if (model.Id == failing.Id)
            {
                model.Status = MLModelStatus.Failed;
                model.IsSuppressed = false;
                model.DegradationRetiredAt = now;
                AddLifecycleLog(
                    db,
                    model,
                    MLModelLifecycleEventType.DegradationRetirement,
                    priorStatus,
                    MLModelStatus.Failed,
                    fallback.Id,
                    reason,
                    now);
                continue;
            }

            model.Status = MLModelStatus.Superseded;
            AddLifecycleLog(
                db,
                model,
                MLModelLifecycleEventType.AutoRollbackDemotion,
                priorStatus,
                MLModelStatus.Superseded,
                fallback.Id,
                $"Demoted during auto-rollback of model {failing.Id}: {reason}",
                now);
        }

        if (activePairModels.All(m => m.Id != failing.Id))
        {
            failing.IsActive = false;
            failing.IsSuppressed = false;
            failing.IsFallbackChampion = false;
            failing.Status = MLModelStatus.Failed;
            failing.DegradationRetiredAt = now;
            AddLifecycleLog(
                db,
                failing,
                MLModelLifecycleEventType.DegradationRetirement,
                previousFailingStatus,
                MLModelStatus.Failed,
                fallback.Id,
                reason,
                now);
        }

        fallback.IsActive = true;
        fallback.IsSuppressed = false;
        fallback.IsFallbackChampion = false;
        fallback.Status = MLModelStatus.Active;
        fallback.ActivatedAt = now;
        AddLifecycleLog(
            db,
            fallback,
            MLModelLifecycleEventType.AutoRollbackPromotion,
            previousFallbackStatus,
            MLModelStatus.Active,
            failing.Id,
            $"Restored as champion after auto-rollback of model {failing.Id}: {reason}",
            now);

        try
        {
            await writeCtx.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogWarning(ex,
                "MLModelAutoRollback: concurrency conflict rolling back model {ModelId}; another process likely already actioned it",
                failingModelId);
            return null;
        }

        return new RollbackAudit(
            FailingModelId: failing.Id,
            FallbackModelId: fallback.Id,
            Symbol: failing.Symbol,
            Timeframe: failing.Timeframe,
            Reason: reason,
            DegradedTriggers: BuildTriggerList(failing, config),
            RolledBackAtUtc: now);
    }

    private async Task<IAsyncDisposable?> TryAcquireCycleLockAsync(
        AutoRollbackRuntimeConfig config,
        CancellationToken ct)
    {
        if (_distributedLock is null)
        {
            return null;
        }

        if (config.LockTimeoutSeconds <= 0)
        {
            return await _distributedLock.TryAcquireAsync(CycleLockKey, ct);
        }

        return await _distributedLock.TryAcquireAsync(
            CycleLockKey,
            TimeSpan.FromSeconds(config.LockTimeoutSeconds),
            ct);
    }

    internal static async Task<AutoRollbackRuntimeConfig> LoadConfigAsync(DbContext db, CancellationToken ct)
    {
        int pollSeconds = Math.Clamp(
            await GetIntAsync(db, CK_PollSeconds, DefaultPollSeconds, ct),
            30,
            86_400);
        int maxRetrainFailures = Math.Clamp(
            await GetIntAsync(db, CK_MaxRetrainFailures, DefaultMaxRetrainFailures, ct),
            1,
            100);
        double maxCalibrationDrift = ClampFinite(
            await GetDoubleAsync(db, CK_MaxCalibrationDrift, DefaultMaxCalibrationDrift, ct),
            0.0,
            10.0,
            DefaultMaxCalibrationDrift);
        decimal minLiveAccuracy = (decimal)ClampFinite(
            await GetDoubleAsync(db, CK_MinLiveDirectionAccuracy, (double)DefaultMinLiveDirectionAccuracy, ct),
            0.0,
            1.0,
            (double)DefaultMinLiveDirectionAccuracy);
        int minLivePredictions = Math.Clamp(
            await GetIntAsync(db, CK_MinLivePredictions, DefaultMinLivePredictions, ct),
            1,
            1_000_000);
        double maxOosDrawdown = ClampFinite(
            await GetDoubleAsync(db, CK_MaxOosDrawdown, DefaultMaxOosDrawdown, ct),
            0.0,
            1.0,
            DefaultMaxOosDrawdown);
        int lockTimeoutSeconds = Math.Clamp(
            await GetIntAsync(db, CK_LockTimeoutSeconds, DefaultLockTimeoutSeconds, ct),
            0,
            300);

        return new AutoRollbackRuntimeConfig(
            Enabled: await GetBoolAsync(db, CK_Enabled, true, ct),
            PollSeconds: pollSeconds,
            MaxRetrainFailures: maxRetrainFailures,
            MaxCalibrationDrift: maxCalibrationDrift,
            MinLiveDirectionAccuracy: minLiveAccuracy,
            MinLivePredictions: minLivePredictions,
            RollbackOnPpcSurprise: await GetBoolAsync(db, CK_RollbackOnPpcSurprise, DefaultRollbackOnPpcSurprise, ct),
            MaxOosDrawdown: maxOosDrawdown,
            LockTimeoutSeconds: lockTimeoutSeconds);
    }

    private static bool IsDegraded(MLModel model, AutoRollbackRuntimeConfig config)
        => model.ConsecutiveRetrainFailures >= config.MaxRetrainFailures
           || model.PlattCalibrationDrift is { } drift && drift > config.MaxCalibrationDrift
           || model.LiveDirectionAccuracy is { } accuracy
              && model.LiveTotalPredictions >= config.MinLivePredictions
              && accuracy < config.MinLiveDirectionAccuracy
           || config.RollbackOnPpcSurprise && model.PpcSurprised
           || model.LatestOosMaxDrawdown is { } drawdown && drawdown > config.MaxOosDrawdown;

    private static string? GetFallbackIneligibilityReason(
        MLModel failing,
        MLModel? fallback,
        AutoRollbackRuntimeConfig config)
    {
        if (fallback is null)
            return "fallback champion does not exist or was deleted";
        if (fallback.Id == failing.Id)
            return "fallback champion points back to the failing model";
        if (!string.Equals(fallback.Symbol, failing.Symbol, StringComparison.OrdinalIgnoreCase))
            return $"fallback symbol {fallback.Symbol} does not match failing symbol {failing.Symbol}";
        if (fallback.Timeframe != failing.Timeframe)
            return $"fallback timeframe {fallback.Timeframe} does not match failing timeframe {failing.Timeframe}";
        if (!string.Equals(fallback.RegimeScope ?? string.Empty, failing.RegimeScope ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            return "fallback regime scope does not match the failing model";
        if (fallback.Status == MLModelStatus.Failed || fallback.DegradationRetiredAt is not null)
            return "fallback champion is already failed or degradation-retired";
        if (fallback.IsSuppressed)
            return "fallback champion is currently suppressed";
        if (fallback.ModelBytes is not { Length: > 0 })
            return "fallback champion has no persisted model bytes for inference";
        if (IsDegraded(fallback, config))
            return "fallback champion also breaches degradation thresholds";

        return null;
    }

    private static string BuildRollbackReason(MLModel failing, AutoRollbackRuntimeConfig config)
    {
        var parts = BuildTriggerList(failing, config);
        return string.Join(" | ", parts);
    }

    private static IReadOnlyList<string> BuildTriggerList(MLModel failing, AutoRollbackRuntimeConfig config)
    {
        var parts = new List<string>();
        if (failing.ConsecutiveRetrainFailures >= config.MaxRetrainFailures)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"ConsecutiveRetrainFailures={failing.ConsecutiveRetrainFailures}>={config.MaxRetrainFailures}"));
        }

        if (failing.PlattCalibrationDrift is { } drift && drift > config.MaxCalibrationDrift)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"PlattCalibrationDrift={drift:F3}>{config.MaxCalibrationDrift:F2}"));
        }

        if (failing.LiveDirectionAccuracy is { } acc
            && failing.LiveTotalPredictions >= config.MinLivePredictions
            && acc < config.MinLiveDirectionAccuracy)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"LiveDirectionAccuracy={acc:F3}<{config.MinLiveDirectionAccuracy:F2} over {failing.LiveTotalPredictions} predictions"));
        }

        if (config.RollbackOnPpcSurprise && failing.PpcSurprised)
        {
            parts.Add("PosteriorPredictiveCheck=surprised");
        }

        if (failing.LatestOosMaxDrawdown is { } drawdown && drawdown > config.MaxOosDrawdown)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"LatestOosMaxDrawdown={drawdown:F3}>{config.MaxOosDrawdown:F2}"));
        }

        return parts;
    }

    private static void AddLifecycleLog(
        DbContext db,
        MLModel model,
        MLModelLifecycleEventType eventType,
        MLModelStatus? previousStatus,
        MLModelStatus newStatus,
        long? previousChampionModelId,
        string reason,
        DateTime occurredAtUtc)
    {
        db.Set<MLModelLifecycleLog>().Add(new MLModelLifecycleLog
        {
            MLModelId = model.Id,
            EventType = eventType,
            PreviousStatus = previousStatus,
            NewStatus = newStatus,
            PreviousChampionModelId = previousChampionModelId,
            Reason = reason.Length <= 1000 ? reason : reason[..1000],
            DirectionAccuracyAtTransition = model.DirectionAccuracy,
            LiveAccuracyAtTransition = model.LiveDirectionAccuracy,
            BrierScoreAtTransition = model.BrierScore,
            OccurredAt = occurredAtUtc
        });
    }

    private async Task SafeLogDecisionAsync(IMediator mediator, RollbackAudit rollback, CancellationToken ct)
    {
        try
        {
            await mediator.Send(new LogDecisionCommand
            {
                Source       = WorkerName,
                EntityType   = "MLModel",
                EntityId     = rollback.FailingModelId,
                DecisionType = "AutoRollback",
                Outcome      = "Rolled back to previous champion",
                Reason       = rollback.Reason,
                ContextJson  = JsonSerializer.Serialize(new
                {
                    rollback.FailingModelId,
                    rollback.FallbackModelId,
                    rollback.Symbol,
                    Timeframe = rollback.Timeframe.ToString(),
                    rollback.RolledBackAtUtc,
                    rollback.DegradedTriggers
                }),
            }, ct);
        }
        catch (Exception ex)
        {
            // Audit failures must never block the rollback itself.
            _logger.LogWarning(ex,
                "MLModelAutoRollback: audit log failed for rollback of model {FailingModelId} to fallback {FallbackModelId}",
                rollback.FailingModelId, rollback.FallbackModelId);
        }
    }

    private static async Task<int> GetIntAsync(DbContext db, string key, int fallback, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>().AsNoTracking()
            .Where(c => c.Key == key && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
    }

    private static async Task<double> GetDoubleAsync(DbContext db, string key, double fallback, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>().AsNoTracking()
            .Where(c => c.Key == key && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : fallback;
    }

    private static async Task<bool> GetBoolAsync(DbContext db, string key, bool fallback, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>().AsNoTracking()
            .Where(c => c.Key == key && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);

        return raw?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "y" or "on" or "enabled" => true,
            "false" or "0" or "no" or "n" or "off" or "disabled" => false,
            _ => fallback
        };
    }

    private static double ClampFinite(double value, double min, double max, double fallback)
    {
        if (!double.IsFinite(value))
        {
            value = fallback;
        }

        return Math.Clamp(value, min, max);
    }
}

internal sealed record AutoRollbackRuntimeConfig(
    bool Enabled,
    int PollSeconds,
    int MaxRetrainFailures,
    double MaxCalibrationDrift,
    decimal MinLiveDirectionAccuracy,
    int MinLivePredictions,
    bool RollbackOnPpcSurprise,
    double MaxOosDrawdown,
    int LockTimeoutSeconds);

internal sealed record AutoRollbackCycleResult(
    int PollSeconds,
    int DegradedModelCount,
    int RollbackCandidateCount,
    int OrphanModelCount,
    int RollbackCount,
    int SkippedRollbackCount,
    string? SkippedReason)
{
    public static AutoRollbackCycleResult Skipped(int pollSeconds, string skippedReason)
        => new(
            PollSeconds: pollSeconds,
            DegradedModelCount: 0,
            RollbackCandidateCount: 0,
            OrphanModelCount: 0,
            RollbackCount: 0,
            SkippedRollbackCount: 0,
            SkippedReason: skippedReason);
}

internal sealed record RollbackAudit(
    long FailingModelId,
    long FallbackModelId,
    string Symbol,
    Timeframe Timeframe,
    string Reason,
    IReadOnlyList<string> DegradedTriggers,
    DateTime RolledBackAtUtc);
