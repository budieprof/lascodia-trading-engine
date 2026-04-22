using System.Diagnostics;
using System.Text.Json;
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
    private const int    AlertPayloadSchemaVersion   = 2;
    private const int    TrainingLogSchemaVersion    = 3;
    private const string MLCpcPretrainerKey = "MLCpcPretrainer";

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
        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = lastPollSecs;
            var cycleStart = Stopwatch.GetTimestamp();

            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                pollSecs = await RunCycleAsync(stoppingToken);
                lastPollSecs = pollSecs;
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
                _metrics?.WorkerErrors.Add(
                    1, new KeyValuePair<string, object?>("worker", WorkerName));
                _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                LogWorkerLoopError(ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _healthMonitor?.RecordWorkerStopped(WorkerName);
        LogWorkerStopping();
    }

    internal async Task<int> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readCtx  = readDb.GetDbContext();
        var writeCtx = writeDb.GetDbContext();

        var config = await _configReader.LoadAsync(readCtx, ct);

        if (!config.Enabled)
        {
            LogCycleDisabled();
            ClearSilentSkipTrackersExcept(null);
            return config.PollSeconds;
        }

        if (await HandleSystemicPauseAsync(writeCtx, config, ct))
            return config.PollSeconds;
        if (await HandleEmbeddingDimMismatchAsync(writeCtx, config, ct))
            return config.PollSeconds;

        var pretrainers = scope.ServiceProvider.GetServices<ICpcPretrainer>().ToList();
        if (!pretrainers.Any(p => p.Kind == config.EncoderType))
        {
            await HandlePretrainerMissingAsync(writeCtx, config, ct);
            return config.PollSeconds;
        }
        else
        {
            _pretrainerMissingConsecutive = 0;
        }

        var candidates = await LoadCandidatePairsAsync(readCtx, config, ct);
        if (candidates.Count == 0)
        {
            LogNoStalePairs();
            return config.PollSeconds;
        }
        await RecordStaleEncoderAlertsAsync(writeCtx, candidates, config, ct);

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
                        writeCtx, candidate, config, CpcReason.LockBusy,
                        candlesLoaded: 0, candlesAfterRegimeFilter: 0,
                        trainingSequences: 0, validationSequences: 0, trainingDurationMs: 0,
                        trainLoss: null, validationLoss: null, promotedEncoderId: null,
                        extraDiagnostics: new Dictionary<string, object?>
                        {
                            ["LockKey"] = BuildCandidateLockKey(candidate, config),
                            ["LockTimeoutSeconds"] = config.LockTimeoutSeconds,
                        },
                        ct: ct);
                    outcome = TrainOutcome.Skipped;
                }
                else
                {
                    outcome = await TrainOnePairAsync(
                        scope.ServiceProvider, writeCtx, candidate, config, pretrainers, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = await RecordUnexpectedCandidateFailureAsync(
                    writeCtx, candidate, config, ex, ct);
            }

            switch (outcome)
            {
                case TrainOutcome.Promoted: trained++; break;
                case TrainOutcome.Rejected: failed++;  break;
                case TrainOutcome.Skipped:  skipped++; break;
            }
        }

        LogCycleComplete(trained, skipped, failed, attempted, throttled, candidates.Count);

        return config.PollSeconds;
    }

    // ── Silent-skip handlers ───────────────────────────────────────────────────

    private async Task<bool> HandleSystemicPauseAsync(
        DbContext writeCtx, MLCpcRuntimeConfig config, CancellationToken ct)
    {
        if (!config.SystemicPauseActive)
        {
            _systemicPauseStartedAt = null;
            _systemicPauseConsecutiveAlerts = 0;
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
            await RaiseConfigurationDriftAlertAsync(
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
        DbContext writeCtx, MLCpcRuntimeConfig config, CancellationToken ct)
    {
        if (config.EmbeddingDim == MLFeatureHelper.CpcEmbeddingBlockSize)
        {
            _embeddingDimMismatchConsecutive = 0;
            return false;
        }

        LogEmbeddingDimMismatch(config.EmbeddingDim, MLFeatureHelper.CpcEmbeddingBlockSize);
        _embeddingDimMismatchConsecutive++;

        if (_embeddingDimMismatchConsecutive >= config.ConfigurationDriftAlertCycles)
        {
            await RaiseConfigurationDriftAlertAsync(
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
        DbContext writeCtx, MLCpcRuntimeConfig config, CancellationToken ct)
    {
        LogPretrainerMissing(config.EncoderType);
        _pretrainerMissingConsecutive++;

        if (_pretrainerMissingConsecutive >= config.ConfigurationDriftAlertCycles)
        {
            await RaiseConfigurationDriftAlertAsync(
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

    private async Task RaiseConfigurationDriftAlertAsync(
        DbContext writeCtx,
        string kind,
        CpcEncoderType encoderType,
        string message,
        IReadOnlyDictionary<string, object?>? extra,
        CancellationToken ct)
    {
        _metrics?.MLCpcConfigurationDriftAlerts.Add(
            1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("encoder_type", encoderType.ToString()));

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var dedupeKey = $"{MLCpcPretrainerKey}:ConfigurationDrift:{kind}:{encoderType}";
        var payload = new Dictionary<string, object?>
        {
            ["SchemaVersion"] = AlertPayloadSchemaVersion,
            ["Kind"]          = kind,
            ["EncoderType"]   = encoderType.ToString(),
            ["Message"]       = message,
        };
        if (extra is not null)
            foreach (var kvp in extra) payload[kvp.Key] = kvp.Value;
        string conditionJson = JsonSerializer.Serialize(payload);

        var existing = await writeCtx.Set<Alert>()
            .Where(a => a.DeduplicationKey == dedupeKey && a.IsActive && !a.IsDeleted)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            existing.ConditionJson = conditionJson;
            existing.LastTriggeredAt = now;
            await TrySaveAlertChangesAsync(writeCtx, dedupeKey, ct);
            return;
        }

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType        = AlertType.ConfigurationDrift,
            Severity         = AlertSeverity.High,
            DeduplicationKey = dedupeKey,
            CooldownSeconds  = 3600,
            ConditionJson    = conditionJson,
            LastTriggeredAt  = now,
            IsActive         = true,
        });
        await TrySaveAlertChangesAsync(writeCtx, dedupeKey, ct);
    }

    // ── Candidate selection ───────────────────────────────────────────────────

    private async Task<List<PairCandidate>> LoadCandidatePairsAsync(
        DbContext readCtx,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        var pairs = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        if (pairs.Count == 0) return new();

        var symbols = pairs.Select(p => p.Symbol).Distinct().ToArray();
        var encoderRows = await readCtx.Set<MLCpcEncoder>()
            .AsNoTracking()
            .Where(e => e.IsActive
                     && !e.IsDeleted
                     && e.EncoderType == config.EncoderType
                     && symbols.Contains(e.Symbol))
            .Select(e => new { e.Id, e.Symbol, e.Timeframe, e.Regime, e.TrainedAt, e.InfoNceLoss })
            .ToListAsync(ct);

        var encoderLookup = encoderRows
            .GroupBy(e => (e.Symbol, e.Timeframe, e.Regime))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Id).First());

        var now    = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.AddHours(-config.RetrainIntervalHours);

        var observedRegimesByPair = new Dictionary<(string Symbol, Timeframe Timeframe), List<CandleMarketRegime>>();
        if (config.TrainPerRegime)
        {
            var observedRegimeRows = await readCtx.Set<MarketRegimeSnapshot>()
                .AsNoTracking()
                .Where(s => symbols.Contains(s.Symbol) && !s.IsDeleted)
                .Select(s => new { s.Symbol, s.Timeframe, s.Regime })
                .Distinct()
                .ToListAsync(ct);

            observedRegimesByPair = observedRegimeRows
                .GroupBy(s => (s.Symbol, s.Timeframe))
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(s => s.Regime).Distinct().OrderBy(r => r).ToList());
        }

        var result = new List<PairCandidate>();
        foreach (var p in pairs)
        {
            var regimes = new List<CandleMarketRegime?> { null };
            if (config.TrainPerRegime &&
                observedRegimesByPair.TryGetValue((p.Symbol, p.Timeframe), out var observedRegimes))
            {
                regimes.AddRange(observedRegimes.Cast<CandleMarketRegime?>());
            }
            if (config.TrainPerRegime)
            {
                regimes.AddRange(encoderRows
                    .Where(e => e.Symbol == p.Symbol && e.Timeframe == p.Timeframe && e.Regime is not null)
                    .Select(e => e.Regime)
                    .Distinct());
                regimes = regimes.Distinct().OrderBy(r => r is null ? -1 : (int)r.Value).ToList();
            }

            foreach (var regime in regimes)
            {
                encoderLookup.TryGetValue((p.Symbol, p.Timeframe, regime), out var existing);
                if (existing is not null && existing.TrainedAt > cutoff)
                    continue;

                result.Add(new PairCandidate(
                    p.Symbol,
                    p.Timeframe,
                    regime,
                    existing?.Id,
                    existing?.InfoNceLoss,
                    existing?.TrainedAt));
            }
        }

        result.Sort((a, b) =>
        {
            if (a.PriorTrainedAt is null && b.PriorTrainedAt is null) return 0;
            if (a.PriorTrainedAt is null) return -1;
            if (b.PriorTrainedAt is null) return 1;
            return a.PriorTrainedAt.Value.CompareTo(b.PriorTrainedAt.Value);
        });

        return result;
    }

    // ── Per-pair training orchestration ───────────────────────────────────────

    private async Task<TrainOutcome> TrainOnePairAsync(
        IServiceProvider scopedProvider,
        DbContext writeCtx,
        PairCandidate candidate,
        MLCpcRuntimeConfig config,
        IReadOnlyList<ICpcPretrainer> pretrainers,
        CancellationToken ct)
    {
        var readDb = scopedProvider.GetRequiredService<IReadApplicationDbContext>();
        var readCtx = readDb.GetDbContext();
        _metrics?.MLCpcCandidates.Add(1, CpcTags(candidate, config));

        var loaded = await LoadAndFilterCandlesAsync(readCtx, candidate, config, ct);
        if (loaded.Candles.Count < loaded.EffectiveMinCandles)
        {
            LogInsufficientCandles(candidate, loaded.Candles.Count, loaded.EffectiveMinCandles);
            return candidate.Regime is null
                ? await RejectCandidateAsync(
                    writeCtx, candidate, config, CpcReason.InsufficientCandles,
                    loaded.CandlesLoaded, loaded.Candles.Count, 0, 0, 0,
                    trainLoss: null, validationLoss: null, promotedEncoderId: null,
                    extraDiagnostics: null, ct)
                : await SkipAndAuditAsync(
                    writeCtx, candidate, config, CpcReason.InsufficientCandles,
                    loaded.CandlesLoaded, loaded.Candles.Count, 0, 0, 0,
                    trainLoss: null, validationLoss: null,
                    extraDiagnostics: null, ct);
        }

        var sequencePreparation = scopedProvider.GetRequiredService<ICpcSequencePreparationService>();
        var sequences = sequencePreparation.BuildSequences(
            loaded.Candles,
            config.SequenceLength,
            config.SequenceStride,
            config.MaxSequences);
        if (sequences.Count == 0)
        {
            LogNoSequences(candidate);
            return candidate.Regime is null
                ? await RejectCandidateAsync(
                    writeCtx, candidate, config, CpcReason.NoSequences,
                    loaded.CandlesLoaded, loaded.Candles.Count, 0, 0, 0,
                    trainLoss: null, validationLoss: null, promotedEncoderId: null,
                    extraDiagnostics: null, ct)
                : await SkipAndAuditAsync(
                    writeCtx, candidate, config, CpcReason.NoSequences,
                    loaded.CandlesLoaded, loaded.Candles.Count, 0, 0, 0,
                    trainLoss: null, validationLoss: null,
                    extraDiagnostics: null, ct);
        }

        var split = SplitSequences(sequences, config);
        if (split.Validation.Count < config.MinValidationSequences || split.Training.Count == 0)
        {
            LogInsufficientValidationSequences(candidate, sequences.Count, split.Validation.Count, config.MinValidationSequences);
            RecordCpcRejection(candidate, config, CpcReason.InsufficientValidationSequences);
            return candidate.Regime is null
                ? await RejectCandidateAsync(
                    writeCtx, candidate, config, CpcReason.InsufficientValidationSequences,
                    loaded.CandlesLoaded, loaded.Candles.Count,
                    split.Training.Count, split.Validation.Count, 0,
                    trainLoss: null, validationLoss: null, promotedEncoderId: null,
                    extraDiagnostics: null, ct)
                : await SkipAndAuditAsync(
                    writeCtx, candidate, config, CpcReason.InsufficientValidationSequences,
                    loaded.CandlesLoaded, loaded.Candles.Count,
                    split.Training.Count, split.Validation.Count, 0,
                    trainLoss: null, validationLoss: null,
                    extraDiagnostics: null, ct);
        }

        RecordCpcSequences(candidate, config, "train", split.Training.Count);
        RecordCpcSequences(candidate, config, "validation", split.Validation.Count);

        var pretrainer = pretrainers.FirstOrDefault(p => p.Kind == config.EncoderType);
        // Cycle-level check already guaranteed a match; this is defence in depth.
        if (pretrainer is null)
        {
            LogPretrainerMissingForCandidate(candidate, config.EncoderType);
            RecordCpcRejection(candidate, config, CpcReason.PretrainerMissing);
            return await SkipAndAuditAsync(
                writeCtx, candidate, config, CpcReason.PretrainerMissing,
                loaded.CandlesLoaded, loaded.Candles.Count,
                split.Training.Count, split.Validation.Count, 0,
                trainLoss: null, validationLoss: null,
                extraDiagnostics: null, ct);
        }

        long trainingDurationMs;
        MLCpcEncoder? newEncoder;
        var trainStart = Stopwatch.GetTimestamp();
        await WorkerBulkhead.MLTraining.WaitAsync(ct);
        try
        {
            newEncoder = await pretrainer.TrainAsync(
                candidate.Symbol,
                candidate.Timeframe,
                split.Training,
                config.EmbeddingDim,
                config.PredictionSteps,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            trainingDurationMs = (long)Stopwatch.GetElapsedTime(trainStart).TotalMilliseconds;
            _metrics?.MLCpcTrainingDurationMs.Record(trainingDurationMs, CpcTags(candidate, config));
            LogTrainerException(candidate, ex);
            RecordCpcRejection(candidate, config, CpcReason.TrainerException);
            return await RejectCandidateAsync(
                writeCtx, candidate, config, CpcReason.TrainerException,
                loaded.CandlesLoaded, loaded.Candles.Count,
                split.Training.Count, split.Validation.Count, trainingDurationMs,
                trainLoss: null, validationLoss: null, promotedEncoderId: null,
                extraDiagnostics: new Dictionary<string, object?>
                {
                    ["ExceptionType"] = ex.GetType().FullName,
                    ["ExceptionMessage"] = ex.Message,
                }, ct);
        }
        finally
        {
            WorkerBulkhead.MLTraining.Release();
        }
        trainingDurationMs = (long)Stopwatch.GetElapsedTime(trainStart).TotalMilliseconds;
        _metrics?.MLCpcTrainingDurationMs.Record(trainingDurationMs, CpcTags(candidate, config));

        if (newEncoder is null)
        {
            LogTrainerReturnedNull(candidate);
            RecordCpcRejection(candidate, config, CpcReason.TrainerReturnedNull);
            return await RejectCandidateAsync(
                writeCtx, candidate, config, CpcReason.TrainerReturnedNull,
                loaded.CandlesLoaded, loaded.Candles.Count,
                split.Training.Count, split.Validation.Count, trainingDurationMs,
                trainLoss: null, validationLoss: null, promotedEncoderId: null,
                extraDiagnostics: null, ct);
        }

        StampFreshEncoder(newEncoder, candidate, config, pretrainer.Kind, split.Training.Count);
        double trainLoss = newEncoder.InfoNceLoss;

        // Shape gates.
        var shapeRejection = EvaluateShapeGates(newEncoder, trainLoss, config);
        if (shapeRejection is { } shapeReason)
        {
            LogShapeGateReject(candidate, shapeReason, trainLoss, config.MaxAcceptableLoss);
            RecordCpcRejection(candidate, config, shapeReason);
            return await RejectCandidateAsync(
                writeCtx, candidate, config, shapeReason,
                loaded.CandlesLoaded, loaded.Candles.Count,
                split.Training.Count, split.Validation.Count, trainingDurationMs,
                trainLoss, validationLoss: null, promotedEncoderId: null,
                extraDiagnostics: null, ct);
        }

        // Full quality-gate suite.
        var gateEvaluator = scopedProvider.GetRequiredService<ICpcEncoderGateEvaluator>();
        CpcEncoderGateResult gateResult;
        try
        {
            gateResult = await gateEvaluator.EvaluateAsync(
                readCtx,
                new CpcEncoderGateRequest(
                    candidate.Symbol,
                    candidate.Timeframe,
                    candidate.Regime,
                    candidate.PriorEncoderId,
                    candidate.PriorInfoNceLoss,
                    newEncoder,
                    split.Training,
                    split.Validation,
                    BuildGateOptions(config)),
                ct);
        }
        catch (Exception ex)
        {
            LogProjectionInvalidThrew(candidate, ex);
            RecordCpcRejection(candidate, config, CpcReason.ProjectionInvalid);
            return await RejectCandidateAsync(
                writeCtx, candidate, config, CpcReason.ProjectionInvalid,
                loaded.CandlesLoaded, loaded.Candles.Count,
                split.Training.Count, split.Validation.Count, trainingDurationMs,
                trainLoss, validationLoss: null, promotedEncoderId: null,
                extraDiagnostics: new Dictionary<string, object?>
                {
                    ["ExceptionType"] = ex.GetType().FullName,
                    ["ExceptionMessage"] = ex.Message,
                }, ct);
        }

        RecordGateMetrics(candidate, config, gateResult);
        if (!gateResult.Passed)
        {
            var rejectReason = ReasonForGateReject(gateResult.Reason);
            LogGateReject(candidate, gateResult.Reason);
            RecordCpcRejection(candidate, config, rejectReason);
            return await RejectCandidateAsync(
                writeCtx, candidate, config, rejectReason,
                loaded.CandlesLoaded, loaded.Candles.Count,
                split.Training.Count, split.Validation.Count, trainingDurationMs,
                trainLoss, gateResult.ValidationInfoNceLoss, promotedEncoderId: null,
                extraDiagnostics: gateResult.Diagnostics, ct);
        }

        newEncoder.InfoNceLoss = gateResult.ValidationInfoNceLoss!.Value;

        // Atomic rotation.
        try
        {
            var promotion = scopedProvider.GetRequiredService<ICpcEncoderPromotionService>();
            var promoteResult = await promotion.PromoteAsync(
                writeCtx,
                new CpcEncoderPromotionRequest(
                    candidate.Symbol,
                    candidate.Timeframe,
                    candidate.Regime,
                    candidate.PriorEncoderId,
                    config.MinImprovement),
                newEncoder,
                ct);
            if (!promoteResult.Promoted)
            {
                return await SkipAndAuditAsync(
                    writeCtx, candidate, config,
                    ParseSupersededReason(promoteResult.Reason),
                    loaded.CandlesLoaded, loaded.Candles.Count,
                    split.Training.Count, split.Validation.Count, trainingDurationMs,
                    trainLoss, gateResult.ValidationInfoNceLoss,
                    extraDiagnostics: new Dictionary<string, object?>
                    {
                        ["CurrentActiveEncoderId"] = promoteResult.CurrentActiveEncoderId,
                        ["CurrentActiveInfoNceLoss"] = promoteResult.CurrentActiveInfoNceLoss,
                    }, ct);
            }
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            LogPromotionConflict(candidate, ex);
            writeCtx.ChangeTracker.Clear();
            return await SkipAndAuditAsync(
                writeCtx, candidate, config, CpcReason.PromotionConflict,
                loaded.CandlesLoaded, loaded.Candles.Count,
                split.Training.Count, split.Validation.Count, trainingDurationMs,
                trainLoss, gateResult.ValidationInfoNceLoss,
                extraDiagnostics: null, ct);
        }

        _metrics?.MLCpcPromotions.Add(1, CpcTags(candidate, config));
        await WriteTrainingLogAsync(
            writeCtx, candidate, config,
            CpcOutcome.Promoted, CpcReason.Accepted,
            loaded.CandlesLoaded, loaded.Candles.Count,
            split.Training.Count, split.Validation.Count, trainingDurationMs,
            trainLoss, gateResult.ValidationInfoNceLoss, newEncoder.Id,
            extraDiagnostics: gateResult.Diagnostics, ct: ct);

        LogEncoderPromoted(
            candidate, trainLoss, gateResult.ValidationInfoNceLoss ?? 0.0,
            split.Training.Count, split.Validation.Count);

        return TrainOutcome.Promoted;
    }

    // ── TrainOnePair helpers ──────────────────────────────────────────────────

    private void StampFreshEncoder(
        MLCpcEncoder encoder, PairCandidate candidate, MLCpcRuntimeConfig config,
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

    private static CpcReason? EvaluateShapeGates(MLCpcEncoder encoder, double trainLoss, MLCpcRuntimeConfig config)
    {
        if (encoder.EmbeddingDim != MLFeatureHelper.CpcEmbeddingBlockSize)
            return CpcReason.EmbeddingDimMismatch;
        if (encoder.EncoderBytes is null || encoder.EncoderBytes.Length == 0)
            return CpcReason.EmptyWeights;
        if (!double.IsFinite(trainLoss) || trainLoss > config.MaxAcceptableLoss)
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
        DbContext readCtx, PairCandidate candidate, MLCpcRuntimeConfig config, CancellationToken ct)
    {
        var candles = await LoadTrainingCandlesAsync(readCtx, candidate, config, ct);
        int candlesLoaded = candles.Count;
        RecordCpcCandles(candidate, config, "loaded", candlesLoaded);

        int effectiveMinCandles = candidate.Regime is null
            ? config.MinCandles
            : config.MinCandlesPerRegime;

        if (candidate.Regime is not null)
            candles = await FilterCandlesByRegimeAsync(readCtx, candles, candidate, config, ct);
        RecordCpcCandles(candidate, config, "regime_filtered", candles.Count);

        return new LoadedCandles(candles, candlesLoaded, effectiveMinCandles);
    }

    private static async Task<List<Candle>> LoadTrainingCandlesAsync(
        DbContext readCtx, PairCandidate candidate, MLCpcRuntimeConfig config, CancellationToken ct)
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
        PairCandidate candidate,
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
        DbContext writeCtx, PairCandidate candidate, MLCpcRuntimeConfig config, CancellationToken ct)
    {
        var lockKey = BuildCandidateLockKey(candidate, config);
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

    private static string BuildCandidateLockKey(PairCandidate candidate, MLCpcRuntimeConfig config)
        => $"{MLCpcPretrainerKey}:{EscapeKeyComponent(candidate.Symbol)}:{candidate.Timeframe}:{candidate.Regime?.ToString() ?? "global"}:{config.EncoderType}";

    private static string EscapeKeyComponent(string value)
        // ':' is our lock-key / dedupe-key separator. '/' appears in crypto symbols and
        // would otherwise visually collide with URL path segments when logs are rendered.
        => value.Replace(':', '_').Replace('/', '_');

    // ── Stale-encoder alerts (pre-loop) ───────────────────────────────────────

    private async Task RecordStaleEncoderAlertsAsync(
        DbContext writeCtx,
        IReadOnlyList<PairCandidate> candidates,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-config.StaleEncoderAlertHours);
        foreach (var candidate in candidates)
        {
            if (candidate.PriorEncoderId is null ||
                candidate.PriorTrainedAt is null ||
                candidate.PriorTrainedAt.Value > cutoff)
            {
                continue;
            }

            _metrics?.MLCpcStaleEncoders.Add(1, CpcTags(candidate, config));

            var regimeLabel = candidate.Regime?.ToString() ?? "global";
            var dedupeKey = $"{MLCpcPretrainerKey}:StaleEncoder:{EscapeKeyComponent(candidate.Symbol)}:{candidate.Timeframe}:{regimeLabel}:{config.EncoderType}";
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var ageHours = Math.Max(0.0, (now - candidate.PriorTrainedAt.Value).TotalHours);
            var conditionJson = JsonSerializer.Serialize(new
            {
                SchemaVersion = AlertPayloadSchemaVersion,
                Message = $"Active CPC encoder for {candidate.Symbol}/{candidate.Timeframe}/{regimeLabel}/{config.EncoderType} is stale ({ageHours:F1}h old).",
                candidate.Symbol,
                Timeframe = candidate.Timeframe.ToString(),
                Regime = regimeLabel,
                EncoderType = config.EncoderType.ToString(),
                PriorEncoderId = candidate.PriorEncoderId,
                PriorTrainedAt = candidate.PriorTrainedAt,
                AgeHours = ageHours,
                StaleEncoderAlertHours = config.StaleEncoderAlertHours,
            });

            var existing = await writeCtx.Set<Alert>()
                .Where(a => a.DeduplicationKey == dedupeKey && a.IsActive && !a.IsDeleted)
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                existing.ConditionJson = conditionJson;
                existing.LastTriggeredAt = now;
                await TrySaveAlertChangesAsync(writeCtx, dedupeKey, ct);
                continue;
            }

            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType = AlertType.DataQualityIssue,
                Severity = AlertSeverity.Medium,
                Symbol = candidate.Symbol,
                DeduplicationKey = dedupeKey,
                CooldownSeconds = 3600,
                ConditionJson = conditionJson,
                LastTriggeredAt = now,
                IsActive = true,
            });
            await TrySaveAlertChangesAsync(writeCtx, dedupeKey, ct);
        }
    }

    // ── Unexpected-failure handler ────────────────────────────────────────────

    private async Task<TrainOutcome> RecordUnexpectedCandidateFailureAsync(
        DbContext writeCtx,
        PairCandidate candidate,
        MLCpcRuntimeConfig config,
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
    {
        int validationCount = Math.Max(
            config.MinValidationSequences,
            (int)Math.Ceiling(sequences.Count * config.ValidationSplit));
        validationCount = Math.Min(validationCount, Math.Max(0, sequences.Count - 1));
        int trainingCount = sequences.Count - validationCount;

        return new SequenceSplit(
            [.. sequences.Take(trainingCount)],
            [.. sequences.Skip(trainingCount).Take(validationCount)]);
    }

    internal static CpcEncoderGateOptions BuildGateOptions(MLCpcRuntimeConfig config)
        => new(
            EmbeddingBlockSize: MLFeatureHelper.CpcEmbeddingBlockSize,
            PredictionSteps: config.PredictionSteps,
            MaxValidationLoss: config.MaxValidationLoss,
            MinValidationEmbeddingL2Norm: config.MinValidationEmbeddingL2Norm,
            MinValidationEmbeddingVariance: config.MinValidationEmbeddingVariance,
            EnableDownstreamProbeGate: config.EnableDownstreamProbeGate,
            MinDownstreamProbeSamples: config.MinDownstreamProbeSamples,
            MinDownstreamProbeBalancedAccuracy: config.MinDownstreamProbeBalancedAccuracy,
            MinDownstreamProbeImprovement: config.MinDownstreamProbeImprovement,
            MinImprovement: config.MinImprovement,
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
        PairCandidate candidate, MLCpcRuntimeConfig config, CpcEncoderGateResult gateResult)
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
        PairCandidate candidate, MLCpcRuntimeConfig config,
        string probeCandidate, double? balancedAccuracy)
    {
        if (balancedAccuracy is not { } value || !double.IsFinite(value))
            return;

        _metrics?.MLCpcDownstreamProbeBalancedAccuracy.Record(
            value,
            CpcTags(candidate, config).Append(new("candidate", probeCandidate)).ToArray());
    }

    private void RecordCpcRejection(PairCandidate candidate, MLCpcRuntimeConfig config, CpcReason reason)
    {
        _metrics?.MLCpcRejections.Add(
            1,
            CpcTags(candidate, config).Append(new("reason", reason.ToWire())).ToArray());
    }

    private void RecordCpcSequences(PairCandidate candidate, MLCpcRuntimeConfig config, string split, int count)
    {
        _metrics?.MLCpcSequences.Record(
            count,
            CpcTags(candidate, config).Append(new("split", split)).ToArray());
    }

    private void RecordCpcCandles(PairCandidate candidate, MLCpcRuntimeConfig config, string stage, int count)
    {
        _metrics?.MLCpcCandles.Record(
            count,
            CpcTags(candidate, config).Append(new("stage", stage)).ToArray());
    }

    private static KeyValuePair<string, object?>[] CpcTags(PairCandidate candidate, MLCpcRuntimeConfig config)
        =>
        [
            new("symbol", candidate.Symbol),
            new("timeframe", candidate.Timeframe.ToString()),
            new("regime", candidate.Regime?.ToString() ?? "global"),
            new("encoder_type", config.EncoderType.ToString()),
        ];

    // ── Audit log + failure counter + alert upsert (transactional) ────────────

    /// <summary>
    /// Writes a rejection audit row, computes consecutive-failure count from the log
    /// history (so the counter survives replica restart), and — if the threshold is hit —
    /// upserts the consecutive-fail alert. All three steps run in one transaction.
    /// </summary>
    private async Task<TrainOutcome> RejectCandidateAsync(
        DbContext writeCtx,
        PairCandidate candidate,
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
        var strategy = writeCtx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async token =>
        {
            await using var tx = await writeCtx.Database.BeginTransactionAsync(token);

            AddTrainingLogEntity(writeCtx, candidate, config,
                CpcOutcome.Rejected, reason,
                candlesLoaded, candlesAfterRegimeFilter,
                trainingSequences, validationSequences, trainingDurationMs,
                trainLoss, validationLoss, promotedEncoderId, extraDiagnostics);
            await writeCtx.SaveChangesAsync(token);

            int count = await CountConsecutiveFailuresAsync(writeCtx, candidate, config, token);
            if (count >= config.ConsecutiveFailAlertThreshold)
                await UpsertConsecutiveFailAlertAsync(writeCtx, candidate, config, reason, count, token);

            await tx.CommitAsync(token);
        }, ct);

        return TrainOutcome.Rejected;
    }

    private async Task<TrainOutcome> SkipAndAuditAsync(
        DbContext writeCtx,
        PairCandidate candidate,
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
        await WriteTrainingLogAsync(
            writeCtx, candidate, config,
            CpcOutcome.Skipped, reason,
            candlesLoaded, candlesAfterRegimeFilter,
            trainingSequences, validationSequences, trainingDurationMs,
            trainLoss, validationLoss, promotedEncoderId: null,
            extraDiagnostics: extraDiagnostics, ct: ct);
        return TrainOutcome.Skipped;
    }

    private Task WriteSkippedLogAsync(
        DbContext writeCtx,
        PairCandidate candidate,
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
        => WriteTrainingLogAsync(
            writeCtx, candidate, config,
            CpcOutcome.Skipped, reason,
            candlesLoaded, candlesAfterRegimeFilter,
            trainingSequences, validationSequences, trainingDurationMs,
            trainLoss, validationLoss, promotedEncoderId,
            extraDiagnostics, ct);

    private async Task WriteTrainingLogAsync(
        DbContext writeCtx,
        PairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcOutcome outcome,
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
        AddTrainingLogEntity(writeCtx, candidate, config,
            outcome, reason,
            candlesLoaded, candlesAfterRegimeFilter,
            trainingSequences, validationSequences, trainingDurationMs,
            trainLoss, validationLoss, promotedEncoderId, extraDiagnostics);
        await writeCtx.SaveChangesAsync(ct);
    }

    private void AddTrainingLogEntity(
        DbContext writeCtx,
        PairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcOutcome outcome,
        CpcReason reason,
        int candlesLoaded,
        int candlesAfterRegimeFilter,
        int trainingSequences,
        int validationSequences,
        long trainingDurationMs,
        double? trainLoss,
        double? validationLoss,
        long? promotedEncoderId,
        IReadOnlyDictionary<string, object?>? extraDiagnostics)
    {
        var diagnostics = BuildTrainingLogDiagnostics(config, extraDiagnostics);

        writeCtx.Set<MLCpcEncoderTrainingLog>().Add(new MLCpcEncoderTrainingLog
        {
            Symbol = candidate.Symbol,
            Timeframe = candidate.Timeframe,
            Regime = candidate.Regime,
            EncoderType = config.EncoderType,
            EvaluatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            Outcome = outcome.ToWire(),
            Reason = reason.ToWire(),
            PriorEncoderId = candidate.PriorEncoderId,
            PriorInfoNceLoss = candidate.PriorInfoNceLoss,
            PromotedEncoderId = promotedEncoderId,
            TrainInfoNceLoss = trainLoss,
            ValidationInfoNceLoss = validationLoss,
            CandlesLoaded = candlesLoaded,
            CandlesAfterRegimeFilter = candlesAfterRegimeFilter,
            TrainingSequences = trainingSequences,
            ValidationSequences = validationSequences,
            TrainingDurationMs = trainingDurationMs,
            DiagnosticsJson = JsonSerializer.Serialize(diagnostics),
        });
    }

    private static Dictionary<string, object?> BuildTrainingLogDiagnostics(
        MLCpcRuntimeConfig config,
        IReadOnlyDictionary<string, object?>? extraDiagnostics)
    {
        var diagnostics = new Dictionary<string, object?>
        {
            ["SchemaVersion"] = TrainingLogSchemaVersion,
            ["SequenceLength"] = config.SequenceLength,
            ["SequenceStride"] = config.SequenceStride,
            ["MaxSequences"] = config.MaxSequences,
            ["ValidationSplit"] = config.ValidationSplit,
            ["MinValidationSequences"] = config.MinValidationSequences,
            ["MaxValidationLoss"] = config.MaxValidationLoss,
            ["MinValidationEmbeddingL2Norm"] = config.MinValidationEmbeddingL2Norm,
            ["MinValidationEmbeddingVariance"] = config.MinValidationEmbeddingVariance,
            ["EnableDownstreamProbeGate"] = config.EnableDownstreamProbeGate,
            ["MinDownstreamProbeSamples"] = config.MinDownstreamProbeSamples,
            ["MinDownstreamProbeBalancedAccuracy"] = config.MinDownstreamProbeBalancedAccuracy,
            ["MinDownstreamProbeImprovement"] = config.MinDownstreamProbeImprovement,
            ["EnableRepresentationDriftGate"] = config.EnableRepresentationDriftGate,
            ["MinCentroidCosineDistance"] = config.MinCentroidCosineDistance,
            ["MaxRepresentationMeanPsi"] = config.MaxRepresentationMeanPsi,
            ["EnableArchitectureSwitchGate"] = config.EnableArchitectureSwitchGate,
            ["MaxArchitectureSwitchAccuracyRegression"] = config.MaxArchitectureSwitchAccuracyRegression,
            ["EnableAdversarialValidationGate"] = config.EnableAdversarialValidationGate,
            ["MaxAdversarialValidationAuc"] = config.MaxAdversarialValidationAuc,
            ["MinAdversarialValidationSamples"] = config.MinAdversarialValidationSamples,
            ["StaleEncoderAlertHours"] = config.StaleEncoderAlertHours,
            ["PredictionSteps"] = config.PredictionSteps,
            ["EmbeddingDim"] = config.EmbeddingDim,
            ["LockTimeoutSeconds"] = config.LockTimeoutSeconds,
            ["RegimeCandleBackfillMultiplier"] = config.RegimeCandleBackfillMultiplier,
            ["ConfigurationDriftAlertCycles"] = config.ConfigurationDriftAlertCycles,
            ["SystemicPauseAlertHours"] = config.SystemicPauseAlertHours,
        };
        if (extraDiagnostics is not null)
        {
            foreach (var kvp in extraDiagnostics)
                diagnostics[kvp.Key] = kvp.Value;
        }
        return diagnostics;
    }

    private static async Task<int> CountConsecutiveFailuresAsync(
        DbContext writeCtx,
        PairCandidate candidate,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        string promotedWire = CpcOutcome.Promoted.ToWire();
        string rejectedWire = CpcOutcome.Rejected.ToWire();

        var lastPromotedAt = await writeCtx.Set<MLCpcEncoderTrainingLog>()
            .AsNoTracking()
            .Where(l => l.Symbol == candidate.Symbol
                     && l.Timeframe == candidate.Timeframe
                     && l.Regime == candidate.Regime
                     && l.EncoderType == config.EncoderType
                     && l.Outcome == promotedWire
                     && !l.IsDeleted)
            .OrderByDescending(l => l.EvaluatedAt)
            .Select(l => (DateTime?)l.EvaluatedAt)
            .FirstOrDefaultAsync(ct);

        var query = writeCtx.Set<MLCpcEncoderTrainingLog>()
            .AsNoTracking()
            .Where(l => l.Symbol == candidate.Symbol
                     && l.Timeframe == candidate.Timeframe
                     && l.Regime == candidate.Regime
                     && l.EncoderType == config.EncoderType
                     && l.Outcome == rejectedWire
                     && !l.IsDeleted);
        if (lastPromotedAt is not null)
            query = query.Where(l => l.EvaluatedAt > lastPromotedAt.Value);

        return await query.CountAsync(ct);
    }

    private async Task UpsertConsecutiveFailAlertAsync(
        DbContext writeCtx,
        PairCandidate candidate,
        MLCpcRuntimeConfig config,
        CpcReason reason,
        int count,
        CancellationToken ct)
    {
        var regimeLabel = candidate.Regime?.ToString() ?? "global";
        var dedupeKey = $"{MLCpcPretrainerKey}:{EscapeKeyComponent(candidate.Symbol)}:{candidate.Timeframe}:{regimeLabel}:{config.EncoderType}";
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var conditionJson = JsonSerializer.Serialize(new
        {
            SchemaVersion = AlertPayloadSchemaVersion,
            Message = $"CPC encoder training failed {count} consecutive cycles for {candidate.Symbol}/{candidate.Timeframe}/{regimeLabel}/{config.EncoderType} (reason={reason.ToWire()}).",
            Symbol = candidate.Symbol,
            Timeframe = candidate.Timeframe.ToString(),
            Regime = regimeLabel,
            EncoderType = config.EncoderType.ToString(),
            Reason = reason.ToWire(),
            ConsecutiveFailures = count,
        });

        var existing = await writeCtx.Set<Alert>()
            .Where(a => a.DeduplicationKey == dedupeKey && a.IsActive && !a.IsDeleted)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            existing.ConditionJson = conditionJson;
            existing.LastTriggeredAt = now;
            await TrySaveAlertChangesAsync(writeCtx, dedupeKey, ct);
            return;
        }

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType = AlertType.DataQualityIssue,
            Severity = AlertSeverity.Medium,
            Symbol = candidate.Symbol,
            DeduplicationKey = dedupeKey,
            CooldownSeconds = 3600,
            ConditionJson = conditionJson,
            LastTriggeredAt = now,
            IsActive = true,
        });
        await TrySaveAlertChangesAsync(writeCtx, dedupeKey, ct);
    }

    /// <summary>
    /// Races across replicas — and across the in-flight promotion's own tx — can push two
    /// processes to try the same Alert upsert. If a unique index kicks in (or any other
    /// DbUpdateException surfaces) we clear the tracker and log rather than tear the cycle.
    /// </summary>
    private async Task TrySaveAlertChangesAsync(DbContext writeCtx, string dedupeKey, CancellationToken ct)
    {
        try
        {
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            LogAlertUpsertRace(dedupeKey, ex);
            writeCtx.ChangeTracker.Clear();
        }
    }

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

    internal sealed record PairCandidate(
        string Symbol,
        Timeframe Timeframe,
        CandleMarketRegime? Regime,
        long? PriorEncoderId,
        double? PriorInfoNceLoss,
        DateTime? PriorTrainedAt);

    private sealed record LoadedCandles(
        List<Candle> Candles,
        int CandlesLoaded,
        int EffectiveMinCandles);
}
