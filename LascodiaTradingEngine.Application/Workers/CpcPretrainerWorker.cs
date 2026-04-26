using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
// A LascodiaTradingEngine.Application.MarketRegime namespace exists, so the simple name
// would resolve to that namespace rather than the enum. Reference the enum via its fully
// qualified name where needed.
using CandleMarketRegime = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Trains and rotates per-(symbol, timeframe, regime) Contrastive Predictive Coding (CPC)
/// encoders. Every cycle the worker:
/// <list type="number">
///   <item>Enumerates (symbol, timeframe[, regime]) pairs with at least one active
///   <see cref="MLModel"/>, picking the most stale candidates.</item>
///   <item>Loads closed candles (with a deeper backfill for rare regimes), builds sliding
///   sequences via <see cref="ICpcSequencePreparationService"/>, and splits time-ordered
///   train/validation.</item>
///   <item>Trains a fresh encoder via the <see cref="ICpcPretrainer"/> whose
///   <see cref="ICpcPretrainer.Kind"/> matches configured <c>MLCpc:EncoderType</c>.</item>
///   <item>Runs the encoder through the full <see cref="ICpcEncoderGateEvaluator"/> gate
///   suite: projection smoke-test, contrastive holdout, embedding-quality, downstream
///   probe, representation-drift (centroid + PSI), cross-architecture anti-forgetting, and
///   adversarial validation.</item>
///   <item>Promotes atomically through <see cref="ICpcEncoderPromotionService"/> on success.</item>
/// </list>
/// Consecutive-failure counts are derived from <see cref="MLCpcEncoderTrainingLog"/> so state
/// survives replica restart. Silent-skip conditions (embedding-dim drift, missing pretrainer,
/// long systemic pause) raise an operator-visible <see cref="AlertType.ConfigurationDrift"/>
/// alert after <c>MLCpc:ConfigurationDriftAlertCycles</c> cycles. CPU-heavy work runs behind
/// <see cref="WorkerBulkhead.MLTraining"/>.
/// </summary>
public sealed partial class CpcPretrainerWorker : BackgroundService
{
    private const string WorkerName = nameof(CpcPretrainerWorker);

    private readonly IServiceScopeFactory         _scopeFactory;
    private readonly ILogger<CpcPretrainerWorker> _logger;
    private readonly TimeProvider                 _timeProvider;
    private readonly IWorkerHealthMonitor?        _healthMonitor;
    private readonly TradingMetrics?              _metrics;
    private readonly MLCpcOptions                 _options;
    private readonly MLCpcConfigReader            _configReader;
    private readonly IDistributedLock             _distributedLock;
    private readonly IDatabaseExceptionClassifier? _dbExceptionClassifier;

    // Silent-skip drift tracking — cleared as soon as the condition clears.
    private int       _embeddingDimMismatchConsecutive;
    private int       _pretrainerMissingConsecutive;
    private DateTime? _systemicPauseStartedAt;
    private int       _systemicPauseConsecutiveAlerts;

    // Cycle-level resilience trackers — see ExecuteAsync for usage.
    private int  _consecutiveFailures;                          // cycles thrown in a row
    private int  _consecutiveZeroPromotionCycles;               // cycles with candidates but zero promotions
    private bool _fleetSystemicAlertActive;                     // current alert open?

    private const string CycleLockKey = "cpc:pretrainer:cycle";
    private const string FleetSystemicDedupeKey = "MLCpc:FleetSystemic";

    private static class EventIds
    {
        public static readonly EventId EncoderPromoted       = new(4301, nameof(EncoderPromoted));
        public static readonly EventId EncoderRejected       = new(4302, nameof(EncoderRejected));
        public static readonly EventId SystemicPauseSkip     = new(4303, nameof(SystemicPauseSkip));
        public static readonly EventId TrainingDataInsufficient = new(4304, nameof(TrainingDataInsufficient));
        public static readonly EventId EmbeddingDimMismatch  = new(4305, nameof(EmbeddingDimMismatch));
        public static readonly EventId ProjectionInvalid     = new(4306, nameof(ProjectionInvalid));
        public static readonly EventId PromotionConflict     = new(4307, nameof(PromotionConflict));
        public static readonly EventId ConfigurationDrift    = new(4308, nameof(ConfigurationDrift));
    }

    public CpcPretrainerWorker(
        IServiceScopeFactory         scopeFactory,
        ILogger<CpcPretrainerWorker> logger,
        IDistributedLock             distributedLock,
        TimeProvider?                timeProvider  = null,
        IWorkerHealthMonitor?        healthMonitor = null,
        TradingMetrics?              metrics       = null,
        MLCpcOptions?                options       = null,
        MLCpcConfigReader?           configReader  = null,
        IDatabaseExceptionClassifier? dbExceptionClassifier = null)
    {
        _scopeFactory    = scopeFactory;
        _logger          = logger;
        _distributedLock = distributedLock ?? throw new ArgumentNullException(nameof(distributedLock));
        _timeProvider    = timeProvider ?? TimeProvider.System;
        _healthMonitor   = healthMonitor;
        _metrics         = metrics;
        _options         = options ?? new MLCpcOptions();
        _configReader    = configReader ?? new MLCpcConfigReader(_options);
        _dbExceptionClassifier = dbExceptionClassifier;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        LogWorkerStarted();
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Trains and rotates per-(symbol, timeframe) CPC encoders on unlabelled candles.",
            TimeSpan.FromSeconds(_options.PollIntervalSeconds));

        int lastPollSecs = _options.PollIntervalSeconds;
        int lastJitterSecs = _options.PollJitterSeconds;
        int lastBackoffShift = _options.FailureBackoffCapShift;
        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = lastPollSecs;
            int jitterSecs = lastJitterSecs;
            int backoffShift = lastBackoffShift;
            var cycleStart = Stopwatch.GetTimestamp();

            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                var cycleOutcome = await RunCycleAsync(stoppingToken);
                pollSecs = cycleOutcome.PollSeconds;
                jitterSecs = cycleOutcome.PollJitterSeconds;
                backoffShift = cycleOutcome.FailureBackoffCapShift;
                lastPollSecs = pollSecs;
                lastJitterSecs = jitterSecs;
                lastBackoffShift = backoffShift;
                _consecutiveFailures = 0;
                _healthMonitor?.RecordCycleSuccess(
                    WorkerName,
                    (long)Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _metrics?.WorkerErrors.Add(
                    1, new KeyValuePair<string, object?>("worker", WorkerName));
                _metrics?.MLCpcConsecutiveCycleFailures.Add(_consecutiveFailures);
                _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                LogWorkerLoopError(ex);
            }

            await Task.Delay(NextDelay(pollSecs, jitterSecs, backoffShift, _consecutiveFailures), stoppingToken);
        }

        _healthMonitor?.RecordWorkerStopped(WorkerName);
        LogWorkerStopping();
    }

    internal async Task<CycleOutcome> RunCycleAsync(CancellationToken ct)
    {
        var cycleStopwatch = Stopwatch.GetTimestamp();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readCtx  = readDb.GetDbContext();
        var writeCtx = writeDb.GetDbContext();
        var auditService = scope.ServiceProvider.GetRequiredService<ICpcPretrainerAuditService>();
        var candidateSelector = scope.ServiceProvider.GetRequiredService<ICpcPretrainerCandidateSelector>();

        var config = await _configReader.LoadAsync(readCtx, ct);
        await auditService.ReconcileDataQualityAlertsAsync(readCtx, writeCtx, config, ct);
        await auditService.ResolveObsoleteConfigurationDriftAlertsAsync(writeCtx, config.EncoderType, ct);

        if (!config.Enabled)
        {
            await auditService.ResolveAllConfigurationDriftAlertsAsync(writeCtx, ct);
            LogCycleDisabled();
            ClearSilentSkipTrackersExcept(null);
            RecordCycleDuration(cycleStopwatch, config, "skipped");
            return CycleOutcome.From(config);
        }

        if (await HandleSystemicPauseAsync(writeCtx, auditService, config, ct))
        {
            RecordCycleDuration(cycleStopwatch, config, "skipped");
            return CycleOutcome.From(config);
        }
        if (await HandleEmbeddingDimMismatchAsync(writeCtx, auditService, config, ct))
        {
            RecordCycleDuration(cycleStopwatch, config, "skipped");
            return CycleOutcome.From(config);
        }

        var pretrainers = scope.ServiceProvider.GetServices<ICpcPretrainer>().ToList();
        if (!pretrainers.Any(p => p.Kind == config.EncoderType))
        {
            await HandlePretrainerMissingAsync(writeCtx, auditService, config, ct);
            RecordCycleDuration(cycleStopwatch, config, "skipped");
            return CycleOutcome.From(config);
        }
        else
        {
            _pretrainerMissingConsecutive = 0;
            await auditService.TryResolveConfigurationDriftAlertAsync(
                writeCtx,
                "pretrainer_missing",
                config.EncoderType,
                ct);
        }

        // Cycle-level distributed lock — when configured, only one replica drives
        // candidate selection + candle loading + per-candidate work per cycle. Other
        // replicas skip and retry after the next jittered poll. Disabling is supported
        // for single-replica deployments via UseCycleLock=false.
        await using var cycleLock = config.UseCycleLock
            ? await TryAcquireCycleLockAsync(config, ct)
            : NoopAsyncDisposable.Instance;
        if (cycleLock is null)
        {
            _metrics?.MLCpcCycleLockAttempts.Add(1, new KeyValuePair<string, object?>("outcome", "busy"));
            LogCycleLockBusy();
            RecordCycleDuration(cycleStopwatch, config, "cycle_lock_busy");
            return CycleOutcome.From(config);
        }
        if (config.UseCycleLock)
            _metrics?.MLCpcCycleLockAttempts.Add(1, new KeyValuePair<string, object?>("outcome", "acquired"));

        var candidates = await candidateSelector.LoadCandidatePairsAsync(readCtx, config, ct);
        if (candidates.Count == 0)
        {
            LogNoStalePairs();
            await ResolveFleetSystemicAlertAsync(writeCtx, ct);
            RecordCycleDuration(cycleStopwatch, config, "ok");
            return CycleOutcome.From(config);
        }
        await auditService.RecordStaleEncoderAlertsAsync(writeCtx, candidates, config, ct);

        // Per-context override hierarchy: load all override keys once and resolve per
        // candidate in memory below. Empty when the feature is disabled, so the resolver
        // becomes a pass-through that returns the global config.
        var overrides = config.OverridesEnabled
            ? await LoadOverridesAsync(readCtx, ct)
            : new ContextOverrideMap();

        int attempted = Math.Min(candidates.Count, config.MaxPairsPerCycle);
        int throttled = candidates.Count - attempted;
        if (throttled > 0)
        {
            _metrics?.MLCpcCandidatesThrottled.Add(
                throttled,
                new KeyValuePair<string, object?>("encoder_type", config.EncoderType.ToString()));
        }

        int trained = 0, skipped = 0, failed = 0;
        foreach (var candidate in candidates.Take(config.MaxPairsPerCycle))
        {
            ct.ThrowIfCancellationRequested();

            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["Symbol"]      = candidate.Symbol,
                ["Timeframe"]   = candidate.Timeframe.ToString(),
                ["Regime"]      = candidate.Regime?.ToString() ?? "global",
                ["EncoderType"] = config.EncoderType.ToString()
            });

            TrainOutcome outcome;
            try
            {
                await using var candidateLock = await TryAcquireCandidateLockAsync(
                    writeCtx, candidate, config, ct);
                if (candidateLock is null)
                {
                    await WriteSkippedLogAsync(
                        auditService,
                        writeCtx, candidate, config, CpcReason.LockBusy,
                        candlesLoaded: 0, candlesAfterRegimeFilter: 0,
                        trainingSequences: 0, validationSequences: 0, trainingDurationMs: 0,
                        trainLoss: null, validationLoss: null, promotedEncoderId: null,
                        extraDiagnostics: new Dictionary<string, object?>
                        {
                            ["LockKey"] = CpcPretrainerKeys.BuildCandidateLockKey(candidate, config),
                            ["LockTimeoutSeconds"] = config.LockTimeoutSeconds,
                        },
                        ct: ct);
                    outcome = TrainOutcome.Skipped;
                }
                else
                {
                    var effective = ResolveEffectiveSettings(
                        overrides, candidate.Symbol, candidate.Timeframe, candidate.Regime, config);
                    outcome = await TrainOnePairAsync(
                        scope.ServiceProvider, writeCtx, candidate, config, effective, pretrainers, auditService, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = await RecordUnexpectedCandidateFailureAsync(
                    writeCtx, candidate, config, auditService, ex, ct);
            }

            switch (outcome)
            {
                case TrainOutcome.Promoted: trained++; break;
                case TrainOutcome.Rejected: failed++;  break;
                case TrainOutcome.Skipped:  skipped++; break;
            }
        }

        LogCycleComplete(trained, skipped, failed, attempted, throttled, candidates.Count);

        // Fleet-systemic gate — when consecutive cycles attempt candidates but never
        // promote any, raise SystemicMLDegradation. Resolves when a single promotion
        // succeeds, signalling the fleet has recovered.
        await UpdateFleetSystemicAlertAsync(writeCtx, auditService, config, attempted, trained, ct);

        RecordCycleDuration(cycleStopwatch, config, "ok");
        return CycleOutcome.From(config);
    }

    // ── Silent-skip handlers ───────────────────────────────────────────────────

    private async Task<bool> HandleSystemicPauseAsync(
        DbContext writeCtx,
        ICpcPretrainerAuditService auditService,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        if (!config.SystemicPauseActive)
        {
            _systemicPauseStartedAt = null;
            _systemicPauseConsecutiveAlerts = 0;
            await auditService.TryResolveConfigurationDriftAlertAsync(
                writeCtx,
                "systemic_pause",
                config.EncoderType,
                ct);
            return false;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _systemicPauseStartedAt ??= now;
        LogSystemicPauseSkip();

        var hoursActive = (now - _systemicPauseStartedAt.Value).TotalHours;
        if (hoursActive >= config.SystemicPauseAlertHours &&
            ++_systemicPauseConsecutiveAlerts >= config.ConfigurationDriftAlertCycles)
        {
            _systemicPauseConsecutiveAlerts = config.ConfigurationDriftAlertCycles; // cap
            await auditService.RaiseConfigurationDriftAlertAsync(
                writeCtx,
                kind: "systemic_pause",
                encoderType: config.EncoderType,
                message: $"MLTraining:SystemicPauseActive has been true for {hoursActive:F1}h (>= {config.SystemicPauseAlertHours}h SLO).",
                extra: new Dictionary<string, object?>
                {
                    ["HoursActive"] = hoursActive,
                    ["StartedAt"]   = _systemicPauseStartedAt.Value,
                    ["AlertThresholdHours"] = config.SystemicPauseAlertHours,
                },
                ct: ct);
        }

        return true;
    }

    private async Task<bool> HandleEmbeddingDimMismatchAsync(
        DbContext writeCtx,
        ICpcPretrainerAuditService auditService,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        if (config.EmbeddingDim == MLFeatureHelper.CpcEmbeddingBlockSize)
        {
            _embeddingDimMismatchConsecutive = 0;
            await auditService.TryResolveConfigurationDriftAlertAsync(
                writeCtx,
                "embedding_dim",
                config.EncoderType,
                ct);
            return false;
        }

        LogEmbeddingDimMismatch(config.EmbeddingDim, MLFeatureHelper.CpcEmbeddingBlockSize);
        _embeddingDimMismatchConsecutive++;

        if (_embeddingDimMismatchConsecutive >= config.ConfigurationDriftAlertCycles)
        {
            await auditService.RaiseConfigurationDriftAlertAsync(
                writeCtx,
                kind: "embedding_dim",
                encoderType: config.EncoderType,
                message: $"MLCpc:EmbeddingDim={config.EmbeddingDim} does not match compile-time CpcEmbeddingBlockSize={MLFeatureHelper.CpcEmbeddingBlockSize}. Training has been skipped for {_embeddingDimMismatchConsecutive} consecutive cycle(s).",
                extra: new Dictionary<string, object?>
                {
                    ["ConfiguredEmbeddingDim"] = config.EmbeddingDim,
                    ["PinnedEmbeddingBlockSize"] = MLFeatureHelper.CpcEmbeddingBlockSize,
                    ["ConsecutiveCycles"] = _embeddingDimMismatchConsecutive,
                },
                ct: ct);
        }
        return true;
    }

    private async Task HandlePretrainerMissingAsync(
        DbContext writeCtx,
        ICpcPretrainerAuditService auditService,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        LogPretrainerMissing(config.EncoderType);
        _pretrainerMissingConsecutive++;

        if (_pretrainerMissingConsecutive >= config.ConfigurationDriftAlertCycles)
        {
            await auditService.RaiseConfigurationDriftAlertAsync(
                writeCtx,
                kind: "pretrainer_missing",
                encoderType: config.EncoderType,
                message: $"No ICpcPretrainer implementation matches MLCpc:EncoderType={config.EncoderType}. Training has been skipped for {_pretrainerMissingConsecutive} consecutive cycle(s).",
                extra: new Dictionary<string, object?>
                {
                    ["ConfiguredEncoderType"] = config.EncoderType.ToString(),
                    ["ConsecutiveCycles"] = _pretrainerMissingConsecutive,
                },
                ct: ct);
        }
    }

    private void ClearSilentSkipTrackersExcept(string? keepActive)
    {
        if (keepActive != "systemic_pause")
        {
            _systemicPauseStartedAt = null;
            _systemicPauseConsecutiveAlerts = 0;
        }
        if (keepActive != "embedding_dim")
            _embeddingDimMismatchConsecutive = 0;
        if (keepActive != "pretrainer_missing")
            _pretrainerMissingConsecutive = 0;
    }

    // ── Per-pair training orchestration ───────────────────────────────────────

    private async Task<TrainOutcome> TrainOnePairAsync(
        IServiceProvider scopedProvider,
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        EffectiveTrainingSettings effective,
        IReadOnlyList<ICpcPretrainer> pretrainers,
        ICpcPretrainerAuditService auditService,
        CancellationToken ct)
    {
        // Pipeline of phases — each one either short-circuits with a TrainOutcome
        // (rejection / skip) or augments ctx and returns null to continue. The top-level
        // method stays a flat sequence so the lifecycle of a candidate reads top-to-
        // bottom in 8 lines instead of 280.
        _metrics?.MLCpcCandidates.Add(1, CpcTags(candidate, config));

        var ctx = new TrainPhaseContext(
            scopedProvider,
            writeCtx,
            scopedProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext(),
            candidate,
            config,
            effective,
            pretrainers,
            auditService,
            ct);

        return await RunCandleLoadPhaseAsync(ctx)
            ?? await RunSequenceBuildPhaseAsync(ctx)
            ?? await RunSequenceSplitPhaseAsync(ctx)
            ?? await RunPretrainerLookupPhaseAsync(ctx)
            ?? await RunTrainingPhaseAsync(ctx)
            ?? await RunShapeGatesPhaseAsync(ctx)
            ?? await RunQualityGatesPhaseAsync(ctx)
            ?? await RunPromotionPhaseAsync(ctx);
    }

    /// <summary>
    /// Mutable per-candidate context threaded through the phase pipeline. Inputs are
    /// init-only; phase outputs (LoadedCandles, sequences, encoder, gate result, ...) are
    /// populated as each phase completes successfully.
    /// </summary>
    private sealed class TrainPhaseContext
    {
        public IServiceProvider ScopedProvider { get; }
        public DbContext WriteCtx { get; }
        public DbContext ReadCtx { get; }
        public CpcPairCandidate Candidate { get; }
        public MLCpcRuntimeConfig Config { get; }
        public EffectiveTrainingSettings Effective { get; }
        public IReadOnlyList<ICpcPretrainer> Pretrainers { get; }
        public ICpcPretrainerAuditService Audit { get; }
        public CancellationToken Ct { get; }

        public LoadedCandles? Loaded { get; set; }
        public IReadOnlyList<float[][]> Sequences { get; set; } = [];
        public SequenceSplit? Split { get; set; }
        public ICpcPretrainer? Pretrainer { get; set; }
        public MLCpcEncoder? NewEncoder { get; set; }
        public double TrainLoss { get; set; }
        public long TrainingDurationMs { get; set; }
        public CpcEncoderGateResult? GateResult { get; set; }

        public TrainPhaseContext(
            IServiceProvider scopedProvider,
            DbContext writeCtx,
            DbContext readCtx,
            CpcPairCandidate candidate,
            MLCpcRuntimeConfig config,
            EffectiveTrainingSettings effective,
            IReadOnlyList<ICpcPretrainer> pretrainers,
            ICpcPretrainerAuditService audit,
            CancellationToken ct)
        {
            ScopedProvider = scopedProvider;
            WriteCtx = writeCtx;
            ReadCtx = readCtx;
            Candidate = candidate;
            Config = config;
            Effective = effective;
            Pretrainers = pretrainers;
            Audit = audit;
            Ct = ct;
        }
    }

    private async Task<TrainOutcome?> RunCandleLoadPhaseAsync(TrainPhaseContext ctx)
    {
        var start = Stopwatch.GetTimestamp();
        ctx.Loaded = await LoadAndFilterCandlesAsync(ctx.ReadCtx, ctx.Candidate, ctx.Config, ctx.Effective, ctx.Ct);
        _metrics?.MLCpcCandleLoadMs.Record(
            Stopwatch.GetElapsedTime(start).TotalMilliseconds, CpcTags(ctx.Candidate, ctx.Config));

        if (ctx.Loaded.Candles.Count >= ctx.Loaded.EffectiveMinCandles)
            return null;

        LogInsufficientCandles(ctx.Candidate, ctx.Loaded.Candles.Count, ctx.Loaded.EffectiveMinCandles);
        return await EmitInsufficientDataOutcomeAsync(ctx, CpcReason.InsufficientCandles, 0, 0, 0, null);
    }

    private async Task<TrainOutcome?> RunSequenceBuildPhaseAsync(TrainPhaseContext ctx)
    {
        var sequencePreparation = ctx.ScopedProvider.GetRequiredService<ICpcSequencePreparationService>();
        var start = Stopwatch.GetTimestamp();
        ctx.Sequences = sequencePreparation.BuildSequences(
            ctx.Loaded!.Candles,
            ctx.Config.SequenceLength,
            ctx.Config.SequenceStride,
            ctx.Config.MaxSequences);
        _metrics?.MLCpcSequenceBuildMs.Record(
            Stopwatch.GetElapsedTime(start).TotalMilliseconds, CpcTags(ctx.Candidate, ctx.Config));

        if (ctx.Sequences.Count > 0)
            return null;

        LogNoSequences(ctx.Candidate);
        return await EmitInsufficientDataOutcomeAsync(ctx, CpcReason.NoSequences, 0, 0, 0, null);
    }

    private async Task<TrainOutcome?> RunSequenceSplitPhaseAsync(TrainPhaseContext ctx)
    {
        ctx.Split = SplitSequences(ctx.Sequences, ctx.Config, ctx.Effective);
        if (ctx.Split.Validation.Count >= ctx.Effective.MinValidationSequences && ctx.Split.Training.Count > 0)
        {
            RecordCpcSequences(ctx.Candidate, ctx.Config, "train", ctx.Split.Training.Count);
            RecordCpcSequences(ctx.Candidate, ctx.Config, "validation", ctx.Split.Validation.Count);
            return null;
        }

        LogInsufficientValidationSequences(ctx.Candidate, ctx.Sequences.Count, ctx.Split.Validation.Count, ctx.Effective.MinValidationSequences);
        RecordCpcRejection(ctx.Candidate, ctx.Config, CpcReason.InsufficientValidationSequences);
        return await EmitInsufficientDataOutcomeAsync(
            ctx, CpcReason.InsufficientValidationSequences,
            ctx.Split.Training.Count, ctx.Split.Validation.Count, 0, null);
    }

    private async Task<TrainOutcome?> RunPretrainerLookupPhaseAsync(TrainPhaseContext ctx)
    {
        ctx.Pretrainer = ctx.Pretrainers.FirstOrDefault(p => p.Kind == ctx.Config.EncoderType);
        if (ctx.Pretrainer is not null)
            return null;

        // Cycle-level check already guaranteed a match; this is defence in depth.
        LogPretrainerMissingForCandidate(ctx.Candidate, ctx.Config.EncoderType);
        RecordCpcRejection(ctx.Candidate, ctx.Config, CpcReason.PretrainerMissing);
        return await SkipAndAuditAsync(
            ctx.Audit,
            ctx.WriteCtx, ctx.Candidate, ctx.Config, CpcReason.PretrainerMissing,
            ctx.Loaded!.CandlesLoaded, ctx.Loaded.Candles.Count,
            ctx.Split!.Training.Count, ctx.Split.Validation.Count, 0,
            trainLoss: null, validationLoss: null,
            extraDiagnostics: null, ctx.Ct);
    }

    private async Task<TrainOutcome?> RunTrainingPhaseAsync(TrainPhaseContext ctx)
    {
        var start = Stopwatch.GetTimestamp();
        await WorkerBulkhead.MLTraining.WaitAsync(ctx.Ct);
        try
        {
            ctx.NewEncoder = await ctx.Pretrainer!.TrainAsync(
                ctx.Candidate.Symbol,
                ctx.Candidate.Timeframe,
                ctx.Split!.Training,
                ctx.Config.EmbeddingDim,
                ctx.Config.PredictionSteps,
                ctx.Ct);
        }
        catch (OperationCanceledException) when (ctx.Ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.TrainingDurationMs = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _metrics?.MLCpcTrainingDurationMs.Record(ctx.TrainingDurationMs, CpcTags(ctx.Candidate, ctx.Config));
            LogTrainerException(ctx.Candidate, ex);
            RecordCpcRejection(ctx.Candidate, ctx.Config, CpcReason.TrainerException);
            return await RejectCandidateAsync(
                ctx.Audit,
                ctx.WriteCtx, ctx.Candidate, ctx.Config, CpcReason.TrainerException,
                ctx.Loaded!.CandlesLoaded, ctx.Loaded.Candles.Count,
                ctx.Split.Training.Count, ctx.Split.Validation.Count, ctx.TrainingDurationMs,
                trainLoss: null, validationLoss: null, promotedEncoderId: null,
                extraDiagnostics: BuildExceptionDiagnostics(ex), ctx.Ct);
        }
        finally
        {
            WorkerBulkhead.MLTraining.Release();
        }
        ctx.TrainingDurationMs = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _metrics?.MLCpcTrainingDurationMs.Record(ctx.TrainingDurationMs, CpcTags(ctx.Candidate, ctx.Config));

        if (ctx.NewEncoder is not null)
        {
            StampFreshEncoder(ctx.NewEncoder, ctx.Candidate, ctx.Config, ctx.Pretrainer!.Kind, ctx.Split!.Training.Count);
            ctx.TrainLoss = ctx.NewEncoder.InfoNceLoss;
            return null;
        }

        LogTrainerReturnedNull(ctx.Candidate);
        RecordCpcRejection(ctx.Candidate, ctx.Config, CpcReason.TrainerReturnedNull);
        return await RejectCandidateAsync(
            ctx.Audit,
            ctx.WriteCtx, ctx.Candidate, ctx.Config, CpcReason.TrainerReturnedNull,
            ctx.Loaded!.CandlesLoaded, ctx.Loaded.Candles.Count,
            ctx.Split!.Training.Count, ctx.Split.Validation.Count, ctx.TrainingDurationMs,
            trainLoss: null, validationLoss: null, promotedEncoderId: null,
            extraDiagnostics: null, ctx.Ct);
    }

    private async Task<TrainOutcome?> RunShapeGatesPhaseAsync(TrainPhaseContext ctx)
    {
        var shapeRejection = EvaluateShapeGates(ctx.NewEncoder!, ctx.TrainLoss, ctx.Effective);
        if (shapeRejection is not { } shapeReason)
            return null;

        LogShapeGateReject(ctx.Candidate, shapeReason, ctx.TrainLoss, ctx.Effective.MaxAcceptableLoss);
        RecordCpcRejection(ctx.Candidate, ctx.Config, shapeReason);
        return await RejectCandidateAsync(
            ctx.Audit,
            ctx.WriteCtx, ctx.Candidate, ctx.Config, shapeReason,
            ctx.Loaded!.CandlesLoaded, ctx.Loaded.Candles.Count,
            ctx.Split!.Training.Count, ctx.Split.Validation.Count, ctx.TrainingDurationMs,
            ctx.TrainLoss, validationLoss: null, promotedEncoderId: null,
            extraDiagnostics: null, ctx.Ct);
    }

    private async Task<TrainOutcome?> RunQualityGatesPhaseAsync(TrainPhaseContext ctx)
    {
        var gateEvaluator = ctx.ScopedProvider.GetRequiredService<ICpcEncoderGateEvaluator>();
        var start = Stopwatch.GetTimestamp();
        try
        {
            ctx.GateResult = await gateEvaluator.EvaluateAsync(
                ctx.ReadCtx,
                new CpcEncoderGateRequest(
                    ctx.Candidate.Symbol,
                    ctx.Candidate.Timeframe,
                    ctx.Candidate.Regime,
                    ctx.Candidate.PriorEncoderId,
                    ctx.Candidate.PriorInfoNceLoss,
                    ctx.NewEncoder!,
                    ctx.Split!.Training,
                    ctx.Split.Validation,
                    BuildGateOptions(ctx.Config, ctx.Effective)),
                ctx.Ct);
        }
        catch (Exception ex)
        {
            _metrics?.MLCpcGateEvaluationMs.Record(
                Stopwatch.GetElapsedTime(start).TotalMilliseconds, CpcTags(ctx.Candidate, ctx.Config));
            LogProjectionInvalidThrew(ctx.Candidate, ex);
            RecordCpcRejection(ctx.Candidate, ctx.Config, CpcReason.GateEvaluationException);
            return await RejectCandidateAsync(
                ctx.Audit,
                ctx.WriteCtx, ctx.Candidate, ctx.Config, CpcReason.GateEvaluationException,
                ctx.Loaded!.CandlesLoaded, ctx.Loaded.Candles.Count,
                ctx.Split.Training.Count, ctx.Split.Validation.Count, ctx.TrainingDurationMs,
                ctx.TrainLoss, validationLoss: null, promotedEncoderId: null,
                extraDiagnostics: BuildExceptionDiagnostics(ex), ctx.Ct);
        }

        _metrics?.MLCpcGateEvaluationMs.Record(
            Stopwatch.GetElapsedTime(start).TotalMilliseconds, CpcTags(ctx.Candidate, ctx.Config));
        RecordGateMetrics(ctx.Candidate, ctx.Config, ctx.GateResult);
        if (ctx.GateResult.Passed)
        {
            ctx.NewEncoder!.InfoNceLoss = ctx.GateResult.ValidationInfoNceLoss!.Value;
            return null;
        }

        var rejectReason = ReasonForGateReject(ctx.GateResult.Reason);
        LogGateReject(ctx.Candidate, ctx.GateResult.Reason);
        RecordCpcRejection(ctx.Candidate, ctx.Config, rejectReason);
        return await RejectCandidateAsync(
            ctx.Audit,
            ctx.WriteCtx, ctx.Candidate, ctx.Config, rejectReason,
            ctx.Loaded!.CandlesLoaded, ctx.Loaded.Candles.Count,
            ctx.Split!.Training.Count, ctx.Split.Validation.Count, ctx.TrainingDurationMs,
            ctx.TrainLoss, ctx.GateResult.ValidationInfoNceLoss, promotedEncoderId: null,
            extraDiagnostics: ctx.GateResult.Diagnostics, ctx.Ct);
    }

    private async Task<TrainOutcome> RunPromotionPhaseAsync(TrainPhaseContext ctx)
    {
        try
        {
            var promotion = ctx.ScopedProvider.GetRequiredService<ICpcEncoderPromotionService>();
            var promoteResult = await promotion.PromoteAsync(
                ctx.WriteCtx,
                new CpcEncoderPromotionRequest(
                    ctx.Candidate.Symbol,
                    ctx.Candidate.Timeframe,
                    ctx.Candidate.Regime,
                    ctx.Candidate.PriorEncoderId,
                    ctx.Effective.MinImprovement,
                    ctx.Candidate.ExpectedActiveEncoderId),
                ctx.NewEncoder!,
                ctx.Ct);
            if (!promoteResult.Promoted)
            {
                return await SkipAndAuditAsync(
                    ctx.Audit,
                    ctx.WriteCtx, ctx.Candidate, ctx.Config,
                    ParseSupersededReason(promoteResult.Reason),
                    ctx.Loaded!.CandlesLoaded, ctx.Loaded.Candles.Count,
                    ctx.Split!.Training.Count, ctx.Split.Validation.Count, ctx.TrainingDurationMs,
                    ctx.TrainLoss, ctx.GateResult!.ValidationInfoNceLoss,
                    extraDiagnostics: new Dictionary<string, object?>
                    {
                        ["CurrentActiveEncoderId"] = promoteResult.CurrentActiveEncoderId,
                        ["CurrentActiveInfoNceLoss"] = promoteResult.CurrentActiveInfoNceLoss,
                    }, ctx.Ct);
            }
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            LogPromotionConflict(ctx.Candidate, ex);
            ctx.WriteCtx.ChangeTracker.Clear();
            return await SkipAndAuditAsync(
                ctx.Audit,
                ctx.WriteCtx, ctx.Candidate, ctx.Config, CpcReason.PromotionConflict,
                ctx.Loaded!.CandlesLoaded, ctx.Loaded.Candles.Count,
                ctx.Split!.Training.Count, ctx.Split.Validation.Count, ctx.TrainingDurationMs,
                ctx.TrainLoss, ctx.GateResult!.ValidationInfoNceLoss,
                extraDiagnostics: null, ctx.Ct);
        }

        _metrics?.MLCpcPromotions.Add(1, CpcTags(ctx.Candidate, ctx.Config));
        await WriteTrainingLogAsync(
            ctx.Audit,
            ctx.WriteCtx, ctx.Candidate, ctx.Config,
            ctx.Loaded!.CandlesLoaded, ctx.Loaded.Candles.Count,
            ctx.Split!.Training.Count, ctx.Split.Validation.Count, ctx.TrainingDurationMs,
            ctx.TrainLoss, ctx.GateResult!.ValidationInfoNceLoss, ctx.NewEncoder!.Id,
            extraDiagnostics: ctx.GateResult.Diagnostics, ct: ctx.Ct);
        await ctx.Audit.TryResolveRecoveredCandidateAlertsAsync(ctx.WriteCtx, ctx.Candidate, ctx.Config, ctx.Ct);

        LogEncoderPromoted(
            ctx.Candidate, ctx.TrainLoss, ctx.GateResult.ValidationInfoNceLoss ?? 0.0,
            ctx.Split.Training.Count, ctx.Split.Validation.Count);

        return TrainOutcome.Promoted;
    }

    /// <summary>
    /// Shared exit point for the early data-availability phases (insufficient candles,
    /// no sequences, insufficient validation sequences). Routes to a soft skip when the
    /// candidate carries a regime (the global pair will still get training); otherwise
    /// records a hard rejection so the consecutive-failure tracker fires.
    /// </summary>
    private Task<TrainOutcome> EmitInsufficientDataOutcomeAsync(
        TrainPhaseContext ctx,
        CpcReason reason,
        int trainingSequences,
        int validationSequences,
        long trainingDurationMs,
        IReadOnlyDictionary<string, object?>? extraDiagnostics)
        => ctx.Candidate.Regime is null
            ? RejectCandidateAsync(
                ctx.Audit,
                ctx.WriteCtx, ctx.Candidate, ctx.Config, reason,
                ctx.Loaded!.CandlesLoaded, ctx.Loaded.Candles.Count,
                trainingSequences, validationSequences, trainingDurationMs,
                trainLoss: null, validationLoss: null, promotedEncoderId: null,
                extraDiagnostics, ctx.Ct)
            : SkipAndAuditAsync(
                ctx.Audit,
                ctx.WriteCtx, ctx.Candidate, ctx.Config, reason,
                ctx.Loaded!.CandlesLoaded, ctx.Loaded.Candles.Count,
                trainingSequences, validationSequences, trainingDurationMs,
                trainLoss: null, validationLoss: null,
                extraDiagnostics, ctx.Ct);

    private static IReadOnlyDictionary<string, object?> BuildExceptionDiagnostics(Exception ex)
        => new Dictionary<string, object?>
        {
            ["ExceptionType"] = ex.GetType().FullName,
            ["ExceptionMessage"] = ex.Message,
        };

    // ── TrainOnePair helpers ──────────────────────────────────────────────────

    private void StampFreshEncoder(
        MLCpcEncoder encoder, CpcPairCandidate candidate, MLCpcRuntimeConfig config,
        CpcEncoderType pretrainerKind, int trainingSampleCount)
    {
        encoder.Symbol = candidate.Symbol;
        encoder.Timeframe = candidate.Timeframe;
        encoder.Regime = candidate.Regime;
        encoder.EncoderType = pretrainerKind;
        encoder.PredictionSteps = config.PredictionSteps;
        encoder.TrainingSamples = trainingSampleCount;
        encoder.TrainedAt = _timeProvider.GetUtcNow().UtcDateTime;
        encoder.IsActive = true;
    }

    private static CpcReason? EvaluateShapeGates(MLCpcEncoder encoder, double trainLoss, EffectiveTrainingSettings effective)
    {
        if (encoder.EmbeddingDim != MLFeatureHelper.CpcEmbeddingBlockSize)
            return CpcReason.EmbeddingDimMismatch;
        if (encoder.EncoderBytes is null || encoder.EncoderBytes.Length == 0)
            return CpcReason.EmptyWeights;
        if (!double.IsFinite(trainLoss) || trainLoss > effective.MaxAcceptableLoss)
            return CpcReason.LossOutOfBounds;
        return null;
    }

    private static CpcReason ReasonForGateReject(string wire) => wire switch
    {
        "projection_invalid"                 => CpcReason.ProjectionInvalid,
        "validation_loss_out_of_bounds"      => CpcReason.ValidationLossOutOfBounds,
        "embedding_collapsed"                => CpcReason.EmbeddingCollapsed,
        "no_improvement"                     => CpcReason.NoImprovement,
        "downstream_probe_below_floor"       => CpcReason.DownstreamProbeBelowFloor,
        "downstream_probe_no_lift"           => CpcReason.DownstreamProbeNoLift,
        "downstream_probe_insufficient_samples" => CpcReason.DownstreamProbeInsufficientSamples,
        "downstream_probe_insufficient_labels"  => CpcReason.DownstreamProbeInsufficientLabels,
        "representation_drift_insufficient"  => CpcReason.RepresentationDriftInsufficient,
        "representation_drift_excessive"     => CpcReason.RepresentationDriftExcessive,
        "architecture_switch_regression"     => CpcReason.ArchitectureSwitchRegression,
        "adversarial_validation_failed"      => CpcReason.AdversarialValidationFailed,
        _                                    => CpcReason.ProjectionInvalid,
    };

    private static CpcReason ParseSupersededReason(string? wire) => wire switch
    {
        "superseded_by_better_active" => CpcReason.SupersededByBetterActive,
        "promotion_conflict"          => CpcReason.PromotionConflict,
        _                             => CpcReason.PromotionConflict,
    };

    // ── Candle loading ────────────────────────────────────────────────────────

    private async Task<LoadedCandles> LoadAndFilterCandlesAsync(
        DbContext readCtx, CpcPairCandidate candidate, MLCpcRuntimeConfig config,
        EffectiveTrainingSettings effective, CancellationToken ct)
    {
        var candles = await LoadTrainingCandlesAsync(readCtx, candidate, config, ct);
        int candlesLoaded = candles.Count;
        RecordCpcCandles(candidate, config, "loaded", candlesLoaded);

        int effectiveMinCandles = candidate.Regime is null
            ? effective.MinCandles
            : config.MinCandlesPerRegime;

        if (candidate.Regime is not null)
            candles = await FilterCandlesByRegimeAsync(readCtx, candles, candidate, config, ct);
        RecordCpcCandles(candidate, config, "regime_filtered", candles.Count);

        return new LoadedCandles(candles, candlesLoaded, effectiveMinCandles);
    }

    private static async Task<List<Candle>> LoadTrainingCandlesAsync(
        DbContext readCtx, CpcPairCandidate candidate, MLCpcRuntimeConfig config, CancellationToken ct)
    {
        int take = candidate.Regime is null
            ? config.TrainingCandles
            : Math.Min(
                config.TrainingCandles * config.RegimeCandleBackfillMultiplier,
                config.TrainingCandles + (config.MinCandlesPerRegime * config.RegimeCandleBackfillMultiplier));

        var candles = await readCtx.Set<Candle>()
            .AsNoTracking()
            .Where(c => c.Symbol == candidate.Symbol
                     && c.Timeframe == candidate.Timeframe
                     && c.IsClosed
                     && !c.IsDeleted)
            .OrderByDescending(c => c.Timestamp)
            .Take(take)
            .ToListAsync(ct);
        candles.Reverse();
        return candles;
    }

    /// <summary>
    /// Partitions candles to those whose timestamp falls under the given regime per the
    /// <see cref="MarketRegimeSnapshot"/> timeline. Uses binary search over the sorted
    /// snapshot array so the filter stays O(n log m) on n candles + m snapshots.
    /// </summary>
    private static async Task<List<Candle>> FilterCandlesByRegimeAsync(
        DbContext readCtx,
        List<Candle> candles,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        if (candles.Count == 0 || candidate.Regime is null) return candles;

        var windowEnd = candles[^1].Timestamp;
        var targetRegime = candidate.Regime.Value;

        var snapshots = await readCtx.Set<MarketRegimeSnapshot>()
            .AsNoTracking()
            .Where(s => s.Symbol == candidate.Symbol
                     && s.Timeframe == candidate.Timeframe
                     && !s.IsDeleted
                     && s.DetectedAt <= windowEnd)
            .OrderBy(s => s.DetectedAt)
            .Select(s => new { s.DetectedAt, s.Regime })
            .ToListAsync(ct);

        if (snapshots.Count == 0) return new();

        var times   = snapshots.Select(s => s.DetectedAt).ToArray();
        var regimes = snapshots.Select(s => s.Regime).ToArray();

        var result = new List<Candle>(candles.Count);
        foreach (var c in candles)
        {
            int idx = Array.BinarySearch(times, c.Timestamp);
            if (idx < 0) idx = ~idx - 1;
            if (idx < 0) continue;
            if (regimes[idx] == targetRegime)
                result.Add(c);
        }

        int keep = Math.Max(config.TrainingCandles, config.MinCandlesPerRegime);
        return result.Count > keep
            ? result.Skip(result.Count - keep).ToList()
            : result;
    }

    // ── Lock acquisition ──────────────────────────────────────────────────────

    private async Task<IAsyncDisposable?> TryAcquireCandidateLockAsync(
        DbContext writeCtx, CpcPairCandidate candidate, MLCpcRuntimeConfig config, CancellationToken ct)
    {
        var lockKey = CpcPretrainerKeys.BuildCandidateLockKey(candidate, config);
        var timeout = TimeSpan.FromSeconds(config.LockTimeoutSeconds);
        var started = Stopwatch.GetTimestamp();
        var lockHandle = await _distributedLock.TryAcquireAsync(lockKey, timeout, ct);
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _metrics?.MLCpcLockAcquisitionMs.Record(
            elapsedMs,
            CpcTags(candidate, config).Append(new("outcome", lockHandle is null ? "busy" : "acquired")).ToArray());
        _metrics?.MLCpcLockAttempts.Add(
            1,
            CpcTags(candidate, config).Append(new("outcome", lockHandle is null ? "busy" : "acquired")).ToArray());

        if (lockHandle is not null)
            return lockHandle;

        LogLockBusy(candidate, config.EncoderType);
        writeCtx.ChangeTracker.Clear();
        return null;
    }

    // ── Unexpected-failure handler ────────────────────────────────────────────

    private async Task<TrainOutcome> RecordUnexpectedCandidateFailureAsync(
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        ICpcPretrainerAuditService auditService,
        Exception ex,
        CancellationToken ct)
    {
        _metrics?.WorkerErrors.Add(
            1, new KeyValuePair<string, object?>("worker", WorkerName));
        LogUnexpectedCandidateFailure(candidate, ex);

        writeCtx.ChangeTracker.Clear();
        try
        {
            RecordCpcRejection(candidate, config, CpcReason.WorkerException);
            return await RejectCandidateAsync(
                auditService,
                writeCtx, candidate, config, CpcReason.WorkerException,
                candlesLoaded: 0, candlesAfterRegimeFilter: 0,
                trainingSequences: 0, validationSequences: 0, trainingDurationMs: 0,
                trainLoss: null, validationLoss: null, promotedEncoderId: null,
                extraDiagnostics: new Dictionary<string, object?>
                {
                    ["ExceptionType"] = ex.GetType().FullName,
                    ["ExceptionMessage"] = ex.Message,
                }, ct);
        }
        catch (Exception logEx) when (logEx is not OperationCanceledException)
        {
            LogFailureAuditPersistFailed(candidate, logEx);
            return TrainOutcome.Rejected;
        }
    }

    // ── Sequence split ────────────────────────────────────────────────────────

    internal static SequenceSplit SplitSequences(IReadOnlyList<float[][]> sequences, MLCpcRuntimeConfig config)
        => SplitSequences(sequences, config, new EffectiveTrainingSettings(
            MinCandles: config.MinCandles,
            MaxAcceptableLoss: config.MaxAcceptableLoss,
            MinImprovement: config.MinImprovement,
            MaxValidationLoss: config.MaxValidationLoss,
            MinValidationSequences: config.MinValidationSequences));

    internal static SequenceSplit SplitSequences(
        IReadOnlyList<float[][]> sequences,
        MLCpcRuntimeConfig config,
        EffectiveTrainingSettings effective)
    {
        int validationCount = Math.Max(
            effective.MinValidationSequences,
            (int)Math.Ceiling(sequences.Count * config.ValidationSplit));
        validationCount = Math.Min(validationCount, Math.Max(0, sequences.Count - 1));
        int trainingCount = sequences.Count - validationCount;

        return new SequenceSplit(
            [.. sequences.Take(trainingCount)],
            [.. sequences.Skip(trainingCount).Take(validationCount)]);
    }

    internal static CpcEncoderGateOptions BuildGateOptions(MLCpcRuntimeConfig config)
        => BuildGateOptions(config, new EffectiveTrainingSettings(
            MinCandles: config.MinCandles,
            MaxAcceptableLoss: config.MaxAcceptableLoss,
            MinImprovement: config.MinImprovement,
            MaxValidationLoss: config.MaxValidationLoss,
            MinValidationSequences: config.MinValidationSequences));

    internal static CpcEncoderGateOptions BuildGateOptions(MLCpcRuntimeConfig config, EffectiveTrainingSettings effective)
        => new(
            EmbeddingBlockSize: MLFeatureHelper.CpcEmbeddingBlockSize,
            PredictionSteps: config.PredictionSteps,
            MaxValidationLoss: effective.MaxValidationLoss,
            MinValidationEmbeddingL2Norm: config.MinValidationEmbeddingL2Norm,
            MinValidationEmbeddingVariance: config.MinValidationEmbeddingVariance,
            EnableDownstreamProbeGate: config.EnableDownstreamProbeGate,
            MinDownstreamProbeSamples: config.MinDownstreamProbeSamples,
            MinDownstreamProbeBalancedAccuracy: config.MinDownstreamProbeBalancedAccuracy,
            MinDownstreamProbeImprovement: config.MinDownstreamProbeImprovement,
            MinImprovement: effective.MinImprovement,
            EnableRepresentationDriftGate: config.EnableRepresentationDriftGate,
            MinCentroidCosineDistance: config.MinCentroidCosineDistance,
            MaxRepresentationMeanPsi: config.MaxRepresentationMeanPsi,
            EnableArchitectureSwitchGate: config.EnableArchitectureSwitchGate,
            MaxArchitectureSwitchAccuracyRegression: config.MaxArchitectureSwitchAccuracyRegression,
            EnableAdversarialValidationGate: config.EnableAdversarialValidationGate,
            MaxAdversarialValidationAuc: config.MaxAdversarialValidationAuc,
            MinAdversarialValidationSamples: config.MinAdversarialValidationSamples);

    // ── Metric helpers ────────────────────────────────────────────────────────

    private void RecordGateMetrics(
        CpcPairCandidate candidate, MLCpcRuntimeConfig config, CpcEncoderGateResult gateResult)
    {
        if (gateResult.ValidationScore is { } validation)
        {
            var tags = CpcTags(candidate, config);
            _metrics?.MLCpcValidationLoss.Record(validation.InfoNceLoss, tags);
            _metrics?.MLCpcValidationEmbeddingL2Norm.Record(validation.MeanL2Norm, tags);
            _metrics?.MLCpcValidationEmbeddingVariance.Record(validation.MeanDimensionVariance, tags);
        }

        RecordDownstreamProbeMetric(candidate, config, "current",
            gateResult.DownstreamProbe.CandidateBalancedAccuracy);
        RecordDownstreamProbeMetric(candidate, config, "prior",
            gateResult.DownstreamProbe.PriorBalancedAccuracy);

        if (gateResult.RepresentationDrift.CentroidCosineDistance is { } centroid &&
            double.IsFinite(centroid))
        {
            _metrics?.MLCpcRepresentationCentroidDistance.Record(centroid, CpcTags(candidate, config));
        }
        if (gateResult.RepresentationDrift.MeanPsi is { } psi && double.IsFinite(psi))
        {
            _metrics?.MLCpcRepresentationMeanPsi.Record(psi, CpcTags(candidate, config));
        }
        if (gateResult.ArchitectureSwitch.Evaluated &&
            gateResult.ArchitectureSwitch.CandidateBalancedAccuracy is { } candAcc &&
            gateResult.ArchitectureSwitch.CrossArchPriorBalancedAccuracy is { } priorAcc)
        {
            _metrics?.MLCpcArchitectureSwitchAccuracyDelta.Record(
                candAcc - priorAcc, CpcTags(candidate, config));
        }
        if (gateResult.AdversarialValidation.Evaluated &&
            gateResult.AdversarialValidation.Auc is { } auc && double.IsFinite(auc))
        {
            _metrics?.MLCpcAdversarialValidationAuc.Record(auc, CpcTags(candidate, config));
        }
    }

    private void RecordDownstreamProbeMetric(
        CpcPairCandidate candidate, MLCpcRuntimeConfig config,
        string probeCandidate, double? balancedAccuracy)
    {
        if (balancedAccuracy is not { } value || !double.IsFinite(value))
            return;

        _metrics?.MLCpcDownstreamProbeBalancedAccuracy.Record(
            value,
            CpcTags(candidate, config).Append(new("candidate", probeCandidate)).ToArray());
    }

    private void RecordCpcRejection(CpcPairCandidate candidate, MLCpcRuntimeConfig config, CpcReason reason)
    {
        _metrics?.MLCpcRejections.Add(
            1,
            CpcTags(candidate, config).Append(new("reason", reason.ToWire())).ToArray());
    }

    private void RecordCpcSequences(CpcPairCandidate candidate, MLCpcRuntimeConfig config, string split, int count)
    {
        _metrics?.MLCpcSequences.Record(
            count,
            CpcTags(candidate, config).Append(new("split", split)).ToArray());
    }

    private void RecordCpcCandles(CpcPairCandidate candidate, MLCpcRuntimeConfig config, string stage, int count)
    {
        _metrics?.MLCpcCandles.Record(
            count,
            CpcTags(candidate, config).Append(new("stage", stage)).ToArray());
    }

    private static KeyValuePair<string, object?>[] CpcTags(CpcPairCandidate candidate, MLCpcRuntimeConfig config)
        =>
        [
            new("symbol", candidate.Symbol),
            new("timeframe", candidate.Timeframe.ToString()),
            new("regime", candidate.Regime?.ToString() ?? "global"),
            new("encoder_type", config.EncoderType.ToString()),
        ];

    // ── Audit log + failure counter + alert upsert (transactional) ────────────

    private async Task<TrainOutcome> RejectCandidateAsync(
        ICpcPretrainerAuditService auditService,
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcReason reason,
        int candlesLoaded,
        int candlesAfterRegimeFilter,
        int trainingSequences,
        int validationSequences,
        long trainingDurationMs,
        double? trainLoss,
        double? validationLoss,
        long? promotedEncoderId,
        IReadOnlyDictionary<string, object?>? extraDiagnostics,
        CancellationToken ct)
    {
        await auditService.RecordRejectedAttemptAsync(
            writeCtx,
            candidate,
            config,
            reason,
            BuildAuditSnapshot(
                candlesLoaded,
                candlesAfterRegimeFilter,
                trainingSequences,
                validationSequences,
                trainingDurationMs,
                trainLoss,
                validationLoss,
                promotedEncoderId,
                extraDiagnostics),
            ct);
        return TrainOutcome.Rejected;
    }

    private async Task<TrainOutcome> SkipAndAuditAsync(
        ICpcPretrainerAuditService auditService,
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcReason reason,
        int candlesLoaded,
        int candlesAfterRegimeFilter,
        int trainingSequences,
        int validationSequences,
        long trainingDurationMs,
        double? trainLoss,
        double? validationLoss,
        IReadOnlyDictionary<string, object?>? extraDiagnostics,
        CancellationToken ct)
    {
        await auditService.RecordSkippedAttemptAsync(
            writeCtx,
            candidate,
            config,
            reason,
            BuildAuditSnapshot(
                candlesLoaded,
                candlesAfterRegimeFilter,
                trainingSequences,
                validationSequences,
                trainingDurationMs,
                trainLoss,
                validationLoss,
                promotedEncoderId: null,
                extraDiagnostics),
            ct);
        return TrainOutcome.Skipped;
    }

    private Task WriteSkippedLogAsync(
        ICpcPretrainerAuditService auditService,
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcReason reason,
        int candlesLoaded,
        int candlesAfterRegimeFilter,
        int trainingSequences,
        int validationSequences,
        long trainingDurationMs,
        double? trainLoss,
        double? validationLoss,
        long? promotedEncoderId,
        IReadOnlyDictionary<string, object?>? extraDiagnostics,
        CancellationToken ct)
        => auditService.RecordSkippedAttemptAsync(
            writeCtx,
            candidate,
            config,
            reason,
            BuildAuditSnapshot(
                candlesLoaded,
                candlesAfterRegimeFilter,
                trainingSequences,
                validationSequences,
                trainingDurationMs,
                trainLoss,
                validationLoss,
                promotedEncoderId,
                extraDiagnostics),
            ct);

    private Task WriteTrainingLogAsync(
        ICpcPretrainerAuditService auditService,
        DbContext writeCtx,
        CpcPairCandidate candidate,
        MLCpcRuntimeConfig config,
        int candlesLoaded,
        int candlesAfterRegimeFilter,
        int trainingSequences,
        int validationSequences,
        long trainingDurationMs,
        double? trainLoss,
        double? validationLoss,
        long? promotedEncoderId,
        IReadOnlyDictionary<string, object?>? extraDiagnostics,
        CancellationToken ct)
        => auditService.RecordPromotedAttemptAsync(
            writeCtx,
            candidate,
            config,
            BuildAuditSnapshot(
                candlesLoaded,
                candlesAfterRegimeFilter,
                trainingSequences,
                validationSequences,
                trainingDurationMs,
                trainLoss,
                validationLoss,
                promotedEncoderId,
                extraDiagnostics),
            ct);

    private static CpcTrainingAttemptSnapshot BuildAuditSnapshot(
        int candlesLoaded,
        int candlesAfterRegimeFilter,
        int trainingSequences,
        int validationSequences,
        long trainingDurationMs,
        double? trainLoss,
        double? validationLoss,
        long? promotedEncoderId,
        IReadOnlyDictionary<string, object?>? extraDiagnostics)
        => new()
        {
            CandlesLoaded = candlesLoaded,
            CandlesAfterRegimeFilter = candlesAfterRegimeFilter,
            TrainingSequences = trainingSequences,
            ValidationSequences = validationSequences,
            TrainingDurationMs = trainingDurationMs,
            TrainLoss = trainLoss,
            ValidationLoss = validationLoss,
            PromotedEncoderId = promotedEncoderId,
            ExtraDiagnostics = extraDiagnostics,
        };

    private bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        if (_dbExceptionClassifier is not null)
            return _dbExceptionClassifier.IsUniqueConstraintViolation(ex);

        // Fallback for tests that do not inject a classifier — reflection-based check keeps
        // Application decoupled from Npgsql when DI does not provide the typed service.
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name == "PostgresException")
            {
                var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
                if (sqlState == "23505")
                    return true;
            }
        }
        return false;
    }

    // ── Supporting types ──────────────────────────────────────────────────────

    internal enum TrainOutcome { Promoted, Rejected, Skipped }

    internal sealed record SequenceSplit(
        IReadOnlyList<float[][]> Training,
        IReadOnlyList<float[][]> Validation);

    private sealed record LoadedCandles(
        List<Candle> Candles,
        int CandlesLoaded,
        int EffectiveMinCandles);
}
