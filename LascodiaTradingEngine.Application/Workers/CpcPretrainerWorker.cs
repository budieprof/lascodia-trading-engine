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

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Trains and rotates per-(symbol, timeframe) Contrastive Predictive Coding (CPC) encoders.
///
/// <para>
/// Every cycle the worker enumerates (symbol, timeframe) pairs that have at least one active
/// <see cref="MLModel"/>, picks the most stale candidates (no encoder, or encoder older than
/// <c>MLCpc:RetrainIntervalHours</c>), loads the last <c>MLCpc:TrainingCandles</c> closed
/// candles, builds raw-OHLCV sequences, trains a fresh encoder via <see cref="ICpcPretrainer"/>,
/// and — if the new InfoNCE loss beats the prior encoder's by <c>MLCpc:MinImprovement</c> —
/// atomically deactivates the old row and inserts the new one.
/// </para>
///
/// <para>
/// Gates the system-wide training pause (<c>MLTraining:SystemicPauseActive</c>) so the encoder
/// workload doesn't fight correlated-failure suppression. CPU-heavy work runs behind
/// <see cref="WorkerBulkhead.MLTraining"/>.
/// </para>
/// </summary>
public sealed class CpcPretrainerWorker : BackgroundService
{
    private const string WorkerName = nameof(CpcPretrainerWorker);
    private const int    AlertPayloadSchemaVersion = 1;
    private const int    RegimeCandleBackfillMultiplier = 8;

    private readonly IServiceScopeFactory        _scopeFactory;
    private readonly ILogger<CpcPretrainerWorker> _logger;
    private readonly TimeProvider                _timeProvider;
    private readonly IWorkerHealthMonitor?       _healthMonitor;
    private readonly TradingMetrics?             _metrics;
    private readonly MLCpcOptions                _options;
    private readonly MLCpcConfigReader           _configReader;

    private readonly Dictionary<(string Symbol, Timeframe Timeframe, global::LascodiaTradingEngine.Domain.Enums.MarketRegime? Regime), int> _consecutiveFailures = new();

    private static class EventIds
    {
        public static readonly EventId EncoderPromoted       = new(4301, nameof(EncoderPromoted));
        public static readonly EventId EncoderRejected       = new(4302, nameof(EncoderRejected));
        public static readonly EventId SystemicPauseSkip     = new(4303, nameof(SystemicPauseSkip));
        public static readonly EventId TrainingDataInsufficient = new(4304, nameof(TrainingDataInsufficient));
        public static readonly EventId EmbeddingDimMismatch  = new(4305, nameof(EmbeddingDimMismatch));
        public static readonly EventId ProjectionInvalid     = new(4306, nameof(ProjectionInvalid));
        public static readonly EventId PromotionConflict     = new(4307, nameof(PromotionConflict));
    }

    public CpcPretrainerWorker(
        IServiceScopeFactory         scopeFactory,
        ILogger<CpcPretrainerWorker> logger,
        TimeProvider?                timeProvider  = null,
        IWorkerHealthMonitor?        healthMonitor = null,
        TradingMetrics?              metrics       = null,
        MLCpcOptions?                options       = null,
        MLCpcConfigReader?           configReader  = null)
    {
        _scopeFactory  = scopeFactory;
        _logger        = logger;
        _timeProvider  = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _metrics       = metrics;
        _options       = options ?? new MLCpcOptions();
        _configReader  = configReader ?? new MLCpcConfigReader(_options);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _logger.LogInformation("CpcPretrainerWorker started.");
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Trains and rotates per-(symbol, timeframe) CPC encoders on unlabelled candles.",
            TimeSpan.FromSeconds(_options.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = _options.PollIntervalSeconds;
            var cycleStart = Stopwatch.GetTimestamp();

            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                pollSecs = await RunCycleAsync(stoppingToken);
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
                _logger.LogError(ex, "CpcPretrainerWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _healthMonitor?.RecordWorkerStopped(WorkerName);
        _logger.LogInformation("CpcPretrainerWorker stopping.");
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
            _logger.LogDebug("CpcPretrainerWorker: disabled via config — skipping cycle.");
            return config.PollSeconds;
        }

        if (config.SystemicPauseActive)
        {
            _logger.LogInformation(
                EventIds.SystemicPauseSkip,
                "CpcPretrainerWorker: skipping cycle because MLTraining:SystemicPauseActive is true.");
            return config.PollSeconds;
        }

        // Guard: the V7 feature vector pins a compile-time embedding block size. If config drifts
        // from it, we refuse to promote — but don't spin retraining with a doomed dim.
        if (config.EmbeddingDim != MLFeatureHelper.CpcEmbeddingBlockSize)
        {
            _logger.LogWarning(
                EventIds.EmbeddingDimMismatch,
                "CpcPretrainerWorker: MLCpc:EmbeddingDim={Configured} does not match " +
                "MLFeatureHelper.CpcEmbeddingBlockSize={Pinned}. Skipping — fix config or rebuild to match.",
                config.EmbeddingDim, MLFeatureHelper.CpcEmbeddingBlockSize);
            return config.PollSeconds;
        }

        var candidates = await LoadCandidatePairsAsync(readCtx, config, ct);
        if (candidates.Count == 0)
        {
            _logger.LogDebug("CpcPretrainerWorker: no stale pairs — nothing to train.");
            return config.PollSeconds;
        }

        int trained = 0, skipped = 0, failed = 0;
        foreach (var candidate in candidates.Take(config.MaxPairsPerCycle))
        {
            ct.ThrowIfCancellationRequested();

            TrainOutcome outcome;
            try
            {
                outcome = await TrainOnePairAsync(
                    scope.ServiceProvider, writeCtx, candidate, config, ct);
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

        _logger.LogInformation(
            "CpcPretrainerWorker cycle complete: trained={Trained} skipped={Skipped} failed={Failed} candidates={Total}",
            trained, skipped, failed, candidates.Count);

        return config.PollSeconds;
    }

    // ── Candidate selection ───────────────────────────────────────────────────

    private async Task<List<PairCandidate>> LoadCandidatePairsAsync(
        DbContext readCtx,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        // Pairs that actually have an active supervised model — no point training an encoder
        // for a pair nothing will consume.
        var pairs = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        if (pairs.Count == 0) return new();

        // One query for all active encoders on those pairs (across all regimes).
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

        // Regimes we consider for training. Without TrainPerRegime we train a single global
        // (null-regime) encoder per pair; with it, we also enumerate each concrete enum value.
        var regimes = config.TrainPerRegime
            ? new List<global::LascodiaTradingEngine.Domain.Enums.MarketRegime?> { null }.Concat(
                Enum.GetValues<global::LascodiaTradingEngine.Domain.Enums.MarketRegime>().Cast<global::LascodiaTradingEngine.Domain.Enums.MarketRegime?>()).ToList()
            : new List<global::LascodiaTradingEngine.Domain.Enums.MarketRegime?> { null };

        var result = new List<PairCandidate>();
        foreach (var p in pairs)
        {
            foreach (var regime in regimes)
            {
                encoderLookup.TryGetValue((p.Symbol, p.Timeframe, regime), out var existing);

                if (existing is not null && existing.TrainedAt > cutoff)
                    continue; // fresh enough — skip

                result.Add(new PairCandidate(
                    p.Symbol,
                    p.Timeframe,
                    regime,
                    existing?.Id,
                    existing?.InfoNceLoss,
                    existing?.TrainedAt));
            }
        }

        // Oldest (or missing) first.
        result.Sort((a, b) =>
        {
            if (a.PriorTrainedAt is null && b.PriorTrainedAt is null) return 0;
            if (a.PriorTrainedAt is null) return -1;
            if (b.PriorTrainedAt is null) return 1;
            return a.PriorTrainedAt.Value.CompareTo(b.PriorTrainedAt.Value);
        });

        return result;
    }

    // ── Per-pair training ─────────────────────────────────────────────────────

    private async Task<TrainOutcome> TrainOnePairAsync(
        IServiceProvider scopedProvider,
        DbContext        writeCtx,
        PairCandidate    candidate,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        var readDb = scopedProvider.GetRequiredService<IReadApplicationDbContext>();
        var readCtx = readDb.GetDbContext();
        _metrics?.MLCpcCandidates.Add(1, CpcTags(candidate, config));

        // Load enough candles to build sequences. Regime-specific candidates may need an
        // expanded historical window because rare regimes are sparse in the latest global tail.
        var candles = await LoadTrainingCandlesAsync(readCtx, candidate, config, ct);
        int candlesLoaded = candles.Count;
        RecordCpcCandles(candidate, config, "loaded", candlesLoaded);

        // Per-regime training partitions candles by the regime active at each candle's
        // timestamp. A regime with too few candles is skipped but doesn't block the
        // other regimes for the same pair.
        int effectiveMinCandles = candidate.Regime is null
            ? config.MinCandles
            : config.MinCandlesPerRegime;

        if (candidate.Regime is not null)
            candles = await FilterCandlesByRegimeAsync(readCtx, candles, candidate, config, ct);
        int candlesAfterRegimeFilter = candles.Count;
        RecordCpcCandles(candidate, config, "regime_filtered", candlesAfterRegimeFilter);

        if (candles.Count < effectiveMinCandles)
        {
            _logger.LogDebug(
                EventIds.TrainingDataInsufficient,
                "CpcPretrainerWorker: {Symbol}/{Timeframe}/{Regime} has {Count} closed candles (<{Min}) — skipping.",
                candidate.Symbol, candidate.Timeframe,
                candidate.Regime?.ToString() ?? "global",
                candles.Count, effectiveMinCandles);
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "skipped", "insufficient_candles",
                candlesLoaded, candlesAfterRegimeFilter, 0, 0, 0,
                trainLoss: null, validationLoss: null, promotedEncoderId: null, ct);
            return TrainOutcome.Skipped;
        }

        var sequences = MLCpcSequenceBuilder.Build(
            candles,
            config.SequenceLength,
            config.SequenceStride,
            config.MaxSequences);
        if (sequences.Count == 0)
        {
            _logger.LogDebug(
                EventIds.TrainingDataInsufficient,
                "CpcPretrainerWorker: {Symbol}/{Timeframe} produced 0 sequences — skipping.",
                candidate.Symbol, candidate.Timeframe);
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "skipped", "no_sequences",
                candlesLoaded, candlesAfterRegimeFilter, 0, 0, 0,
                trainLoss: null, validationLoss: null, promotedEncoderId: null, ct);
            return TrainOutcome.Skipped;
        }

        var split = SplitSequences(sequences, config);
        if (split.Validation.Count < config.MinValidationSequences || split.Training.Count == 0)
        {
            _logger.LogInformation(
                EventIds.TrainingDataInsufficient,
                "CpcPretrainerWorker: {Symbol}/{Timeframe}/{Regime} produced {Total} sequences but only {Validation} validation sequences (<{Min}) — skipping.",
                candidate.Symbol,
                candidate.Timeframe,
                candidate.Regime?.ToString() ?? "global",
                sequences.Count,
                split.Validation.Count,
                config.MinValidationSequences);
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "skipped", "insufficient_validation_sequences",
                candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, 0,
                trainLoss: null, validationLoss: null, promotedEncoderId: null, ct);
            RecordCpcRejection(candidate, config, "insufficient_validation_sequences");
            return TrainOutcome.Skipped;
        }

        RecordCpcSequences(candidate, config, "train", split.Training.Count);
        RecordCpcSequences(candidate, config, "validation", split.Validation.Count);

        var pretrainers = scopedProvider.GetServices<ICpcPretrainer>().ToList();
        var pretrainer = pretrainers.FirstOrDefault(p => p.Kind == config.EncoderType);
        if (pretrainer is null)
        {
            _logger.LogWarning(
                EventIds.EncoderRejected,
                "CpcPretrainerWorker: no ICpcPretrainer registered for EncoderType={EncoderType} — skipping {Symbol}/{Timeframe}/{Regime}.",
                config.EncoderType, candidate.Symbol, candidate.Timeframe,
                candidate.Regime?.ToString() ?? "global");
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "skipped", "pretrainer_missing",
                candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, 0,
                trainLoss: null, validationLoss: null, promotedEncoderId: null, ct);
            RecordCpcRejection(candidate, config, "pretrainer_missing");
            return TrainOutcome.Skipped;
        }

        var trainStart = Stopwatch.GetTimestamp();
        MLCpcEncoder? newEncoder;
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
            long failedTrainingDurationMs = (long)Stopwatch.GetElapsedTime(trainStart).TotalMilliseconds;
            _metrics?.MLCpcTrainingDurationMs.Record(
                failedTrainingDurationMs,
                CpcTags(candidate, config));
            _logger.LogWarning(
                EventIds.EncoderRejected,
                ex,
                "CpcPretrainerWorker: {Symbol}/{Timeframe}/{Regime} pretrainer threw — rejected.",
                candidate.Symbol,
                candidate.Timeframe,
                candidate.Regime?.ToString() ?? "global");
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "rejected", "trainer_exception",
                candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, failedTrainingDurationMs,
                trainLoss: null, validationLoss: null, promotedEncoderId: null, ct);
            RecordCpcRejection(candidate, config, "trainer_exception");
            return await RecordFailureAsync(writeCtx, candidate, "trainer_exception", config, ct);
        }
        finally
        {
            WorkerBulkhead.MLTraining.Release();
        }
        long trainingDurationMs = (long)Stopwatch.GetElapsedTime(trainStart).TotalMilliseconds;
        _metrics?.MLCpcTrainingDurationMs.Record(
            trainingDurationMs,
            CpcTags(candidate, config));

        if (newEncoder is null)
        {
            _logger.LogWarning(
                EventIds.EncoderRejected,
                "CpcPretrainerWorker: {Symbol}/{Timeframe}/{Regime} pretrainer returned null — rejected.",
                candidate.Symbol,
                candidate.Timeframe,
                candidate.Regime?.ToString() ?? "global");
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "rejected", "trainer_returned_null",
                candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, trainingDurationMs,
                trainLoss: null, validationLoss: null, promotedEncoderId: null, ct);
            RecordCpcRejection(candidate, config, "trainer_returned_null");
            return await RecordFailureAsync(writeCtx, candidate, "trainer_returned_null", config, ct);
        }

        // Stamp regime and encoder type on the freshly-trained row. CpcPretrainer* itself is
        // regime-agnostic — the split-by-regime happened at candle selection above — and the
        // Kind-matched pretrainer already set EncoderType, but we re-stamp to be defensive.
        newEncoder.Symbol = candidate.Symbol;
        newEncoder.Timeframe = candidate.Timeframe;
        newEncoder.Regime = candidate.Regime;
        newEncoder.EncoderType = pretrainer.Kind;
        newEncoder.IsActive = true;

        // Shape + improvement gates.
        if (newEncoder.EmbeddingDim != MLFeatureHelper.CpcEmbeddingBlockSize)
        {
            _logger.LogWarning(
                EventIds.EmbeddingDimMismatch,
                "CpcPretrainerWorker: trained encoder dim {Dim} != pinned block size {Pinned} — rejected.",
                newEncoder.EmbeddingDim, MLFeatureHelper.CpcEmbeddingBlockSize);
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "rejected", "embedding_dim_mismatch",
                candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, trainingDurationMs,
                newEncoder.InfoNceLoss, validationLoss: null, promotedEncoderId: null, ct);
            RecordCpcRejection(candidate, config, "embedding_dim_mismatch");
            return await RecordFailureAsync(writeCtx, candidate, "embedding_dim_mismatch", config, ct);
        }

        if (newEncoder.EncoderBytes is null || newEncoder.EncoderBytes.Length == 0)
        {
            _logger.LogWarning(
                EventIds.EncoderRejected,
                "CpcPretrainerWorker: trained encoder has no weight payload — rejected.");
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "rejected", "empty_weights",
                candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, trainingDurationMs,
                newEncoder.InfoNceLoss, validationLoss: null, promotedEncoderId: null, ct);
            RecordCpcRejection(candidate, config, "empty_weights");
            return await RecordFailureAsync(writeCtx, candidate, "empty_weights", config, ct);
        }

        if (!double.IsFinite(newEncoder.InfoNceLoss) ||
            newEncoder.InfoNceLoss > config.MaxAcceptableLoss)
        {
            _logger.LogWarning(
                EventIds.EncoderRejected,
                "CpcPretrainerWorker: {Symbol}/{Timeframe} rejected — loss={Loss} (max={Max}).",
                candidate.Symbol, candidate.Timeframe, newEncoder.InfoNceLoss, config.MaxAcceptableLoss);
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "rejected", "loss_out_of_bounds",
                candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, trainingDurationMs,
                newEncoder.InfoNceLoss, validationLoss: null, promotedEncoderId: null, ct);
            RecordCpcRejection(candidate, config, "loss_out_of_bounds");
            return await RecordFailureAsync(writeCtx, candidate, "loss_out_of_bounds", config, ct);
        }

        var projection = scopedProvider.GetRequiredService<ICpcEncoderProjection>();
        double validationLoss;
        try
        {
            var smoke = projection.ProjectLatest(newEncoder, split.Validation[0]);
            if (smoke.Length != MLFeatureHelper.CpcEmbeddingBlockSize ||
                smoke.Any(v => !float.IsFinite(v)))
            {
                _logger.LogWarning(
                    EventIds.ProjectionInvalid,
                    "CpcPretrainerWorker: {Symbol}/{Timeframe}/{Regime} projection smoke-test failed — length={Length}, expected={Expected}.",
                    candidate.Symbol,
                    candidate.Timeframe,
                    candidate.Regime?.ToString() ?? "global",
                    smoke.Length,
                    MLFeatureHelper.CpcEmbeddingBlockSize);
                await WriteTrainingLogAsync(
                    writeCtx, candidate, config, "rejected", "projection_invalid",
                    candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, trainingDurationMs,
                    newEncoder.InfoNceLoss, validationLoss: null, promotedEncoderId: null, ct);
                RecordCpcRejection(candidate, config, "projection_invalid");
                return await RecordFailureAsync(writeCtx, candidate, "projection_invalid", config, ct);
            }

            validationLoss = ComputeHoldoutContrastiveLoss(
                projection, newEncoder, split.Validation, config.PredictionSteps);
            _metrics?.MLCpcValidationLoss.Record(validationLoss, CpcTags(candidate, config));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                EventIds.ProjectionInvalid,
                ex,
                "CpcPretrainerWorker: {Symbol}/{Timeframe}/{Regime} projection smoke-test threw — rejected.",
                candidate.Symbol,
                candidate.Timeframe,
                candidate.Regime?.ToString() ?? "global");
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "rejected", "projection_invalid",
                candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, trainingDurationMs,
                newEncoder.InfoNceLoss, validationLoss: null, promotedEncoderId: null, ct);
            RecordCpcRejection(candidate, config, "projection_invalid");
            return await RecordFailureAsync(writeCtx, candidate, "projection_invalid", config, ct);
        }

        if (!double.IsFinite(validationLoss) || validationLoss > config.MaxValidationLoss)
        {
            _logger.LogWarning(
                EventIds.EncoderRejected,
                "CpcPretrainerWorker: {Symbol}/{Timeframe}/{Regime} rejected — validation loss={ValidationLoss:F4} (max={Max}).",
                candidate.Symbol,
                candidate.Timeframe,
                candidate.Regime?.ToString() ?? "global",
                validationLoss,
                config.MaxValidationLoss);
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "rejected", "validation_loss_out_of_bounds",
                candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, trainingDurationMs,
                newEncoder.InfoNceLoss, validationLoss, promotedEncoderId: null, ct);
            RecordCpcRejection(candidate, config, "validation_loss_out_of_bounds");
            return await RecordFailureAsync(writeCtx, candidate, "validation_loss_out_of_bounds", config, ct);
        }

        if (candidate.PriorInfoNceLoss is { } prior)
        {
            double threshold = prior * (1.0 - config.MinImprovement);
            if (newEncoder.InfoNceLoss >= threshold)
            {
                _logger.LogInformation(
                    EventIds.EncoderRejected,
                    "CpcPretrainerWorker: {Symbol}/{Timeframe} loss {New:F4} did not beat prior {Prior:F4} by {Pct:P0} — rejected.",
                    candidate.Symbol, candidate.Timeframe,
                    newEncoder.InfoNceLoss, prior, config.MinImprovement);
                await WriteTrainingLogAsync(
                    writeCtx, candidate, config, "rejected", "no_improvement",
                    candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, trainingDurationMs,
                    newEncoder.InfoNceLoss, validationLoss, promotedEncoderId: null, ct);
                RecordCpcRejection(candidate, config, "no_improvement");
                return await RecordFailureAsync(writeCtx, candidate, "no_improvement", config, ct);
            }
        }

        // Passed all gates — rotate atomically.
        try
        {
            var promoteResult = await PromoteEncoderAsync(writeCtx, candidate, newEncoder, config, ct);
            if (promoteResult != PromotionResult.Promoted)
            {
                var reason = promoteResult == PromotionResult.SupersededByBetterActive
                    ? "superseded_by_better_active"
                    : "promotion_conflict";
                await WriteTrainingLogAsync(
                    writeCtx, candidate, config, "skipped", reason,
                    candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, trainingDurationMs,
                    newEncoder.InfoNceLoss, validationLoss, promotedEncoderId: null, ct);
                return TrainOutcome.Skipped;
            }
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            _logger.LogInformation(
                EventIds.PromotionConflict,
                ex,
                "CpcPretrainerWorker: another worker promoted {Symbol}/{Timeframe}/{Regime} first; dropping duplicate candidate.",
                candidate.Symbol,
                candidate.Timeframe,
                candidate.Regime?.ToString() ?? "global");
            writeCtx.ChangeTracker.Clear();
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "skipped", "promotion_conflict",
                candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, trainingDurationMs,
                newEncoder.InfoNceLoss, validationLoss, promotedEncoderId: null, ct);
            return TrainOutcome.Skipped;
        }

        _consecutiveFailures.Remove((candidate.Symbol, candidate.Timeframe, candidate.Regime));
        _metrics?.MLCpcPromotions.Add(1, CpcTags(candidate, config));
        await WriteTrainingLogAsync(
            writeCtx, candidate, config, "promoted", "accepted",
            candlesLoaded, candlesAfterRegimeFilter, split.Training.Count, split.Validation.Count, trainingDurationMs,
            newEncoder.InfoNceLoss, validationLoss, newEncoder.Id, ct);

        _logger.LogInformation(
            EventIds.EncoderPromoted,
            "CpcPretrainerWorker: promoted encoder for {Symbol}/{Timeframe}/{Regime} loss={Loss:F4} validationLoss={ValidationLoss:F4} trainSequences={TrainSequences} validationSequences={ValidationSequences}.",
            candidate.Symbol, candidate.Timeframe,
            candidate.Regime?.ToString() ?? "global",
            newEncoder.InfoNceLoss, validationLoss, split.Training.Count, split.Validation.Count);

        return TrainOutcome.Promoted;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
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

    private async Task<TrainOutcome> RecordUnexpectedCandidateFailureAsync(
        DbContext writeCtx,
        PairCandidate candidate,
        MLCpcRuntimeConfig config,
        Exception ex,
        CancellationToken ct)
    {
        _metrics?.WorkerErrors.Add(
            1, new KeyValuePair<string, object?>("worker", WorkerName));
        _logger.LogError(
            ex,
            "CpcPretrainerWorker: {Symbol}/{Timeframe}/{Regime} candidate failed unexpectedly.",
            candidate.Symbol,
            candidate.Timeframe,
            candidate.Regime?.ToString() ?? "global");

        writeCtx.ChangeTracker.Clear();
        try
        {
            await WriteTrainingLogAsync(
                writeCtx, candidate, config, "rejected", "worker_exception",
                candlesLoaded: 0, candlesAfterRegimeFilter: 0,
                trainingSequences: 0, validationSequences: 0, trainingDurationMs: 0,
                trainLoss: null, validationLoss: null, promotedEncoderId: null, ct);
            RecordCpcRejection(candidate, config, "worker_exception");
            return await RecordFailureAsync(writeCtx, candidate, "worker_exception", config, ct);
        }
        catch (Exception logEx) when (logEx is not OperationCanceledException)
        {
            _logger.LogError(
                logEx,
                "CpcPretrainerWorker: failed to persist unexpected-failure audit row for {Symbol}/{Timeframe}/{Regime}.",
                candidate.Symbol,
                candidate.Timeframe,
                candidate.Regime?.ToString() ?? "global");
            return TrainOutcome.Rejected;
        }
    }

    private static SequenceSplit SplitSequences(IReadOnlyList<float[][]> sequences, MLCpcRuntimeConfig config)
    {
        int validationCount = Math.Max(
            config.MinValidationSequences,
            (int)Math.Ceiling(sequences.Count * config.ValidationSplit));
        validationCount = Math.Min(validationCount, Math.Max(0, sequences.Count - 1));
        int trainingCount = sequences.Count - validationCount;

        return new SequenceSplit(
            sequences.Take(trainingCount).ToArray(),
            sequences.Skip(trainingCount).Take(validationCount).ToArray());
    }

    private static double ComputeHoldoutContrastiveLoss(
        ICpcEncoderProjection projection,
        MLCpcEncoder encoder,
        IReadOnlyList<float[][]> validationSequences,
        int predictionSteps)
    {
        if (validationSequences.Count == 0)
            return double.NaN;

        var projected = validationSequences
            .Select(seq => projection.ProjectSequence(encoder, seq))
            .Where(seq => seq.Length > predictionSteps + 1)
            .ToArray();
        if (projected.Length == 0)
            return double.NaN;

        const int Negatives = 9;
        double totalLoss = 0.0;
        int samples = 0;

        for (int s = 0; s < projected.Length; s++)
        {
            var seq = projected[s];
            int t = Math.Max(0, (seq.Length - predictionSteps - 1) / 2);

            for (int k = 1; k <= predictionSteps && t + k < seq.Length; k++)
            {
                var context = seq[t];
                var positive = seq[t + k];
                double sPos = Dot(context, positive);

                var sNeg = new double[Negatives];
                for (int j = 0; j < Negatives; j++)
                {
                    var negSeq = projected[(s + j + 1) % projected.Length];
                    var neg = negSeq[Math.Min(negSeq.Length - 1, t + k)];
                    sNeg[j] = Dot(context, neg);
                }

                double maxScore = Math.Max(sPos, sNeg.Max());
                double sumExp = Math.Exp(sPos - maxScore);
                for (int j = 0; j < Negatives; j++)
                    sumExp += Math.Exp(sNeg[j] - maxScore);

                totalLoss += Math.Log(sumExp) + maxScore - sPos;
                samples++;
            }
        }

        return samples > 0 ? totalLoss / samples : double.NaN;
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0.0;
        int length = Math.Min(a.Length, b.Length);
        for (int i = 0; i < length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    private void RecordCpcRejection(
        PairCandidate candidate,
        MLCpcRuntimeConfig config,
        string reason)
    {
        _metrics?.MLCpcRejections.Add(
            1,
            CpcTags(candidate, config).Append(new("reason", reason)).ToArray());
    }

    private void RecordCpcSequences(
        PairCandidate candidate,
        MLCpcRuntimeConfig config,
        string split,
        int count)
    {
        _metrics?.MLCpcSequences.Record(
            count,
            CpcTags(candidate, config).Append(new("split", split)).ToArray());
    }

    private void RecordCpcCandles(
        PairCandidate candidate,
        MLCpcRuntimeConfig config,
        string stage,
        int count)
    {
        _metrics?.MLCpcCandles.Record(
            count,
            CpcTags(candidate, config).Append(new("stage", stage)).ToArray());
    }

    private static KeyValuePair<string, object?>[] CpcTags(
        PairCandidate candidate,
        MLCpcRuntimeConfig config)
        =>
        [
            new("symbol", candidate.Symbol),
            new("timeframe", candidate.Timeframe.ToString()),
            new("regime", candidate.Regime?.ToString() ?? "global"),
            new("encoder_type", config.EncoderType.ToString()),
        ];

    private async Task WriteTrainingLogAsync(
        DbContext writeCtx,
        PairCandidate candidate,
        MLCpcRuntimeConfig config,
        string outcome,
        string reason,
        int candlesLoaded,
        int candlesAfterRegimeFilter,
        int trainingSequences,
        int validationSequences,
        long trainingDurationMs,
        double? trainLoss,
        double? validationLoss,
        long? promotedEncoderId,
        CancellationToken ct)
    {
        writeCtx.Set<MLCpcEncoderTrainingLog>().Add(new MLCpcEncoderTrainingLog
        {
            Symbol = candidate.Symbol,
            Timeframe = candidate.Timeframe,
            Regime = candidate.Regime,
            EncoderType = config.EncoderType,
            EvaluatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            Outcome = outcome,
            Reason = reason,
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
            DiagnosticsJson = JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                config.SequenceLength,
                config.SequenceStride,
                config.MaxSequences,
                config.ValidationSplit,
                config.MinValidationSequences,
                config.MaxValidationLoss,
                config.PredictionSteps,
                config.EmbeddingDim,
            }),
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    private async Task<PromotionResult> PromoteEncoderAsync(
        DbContext writeCtx,
        PairCandidate candidate,
        MLCpcEncoder newEncoder,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        var result = PromotionResult.Promoted;
        var strategy = writeCtx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async token =>
        {
            await using var tx = await writeCtx.Database.BeginTransactionAsync(token);

            // Deactivate any currently active rows for this (Symbol, Timeframe, Regime).
            // Scoping on Regime is critical: a global encoder and a per-regime encoder are
            // distinct rows — a new Crisis-regime encoder must NOT deactivate the global one.
            var existingActive = await writeCtx.Set<MLCpcEncoder>()
                .Where(e => e.Symbol == candidate.Symbol
                         && e.Timeframe == candidate.Timeframe
                         && e.Regime == candidate.Regime
                         && e.IsActive
                         && !e.IsDeleted)
                .OrderByDescending(e => e.TrainedAt)
                .ThenByDescending(e => e.Id)
                .ToListAsync(token);

            var currentActive = existingActive.FirstOrDefault();
            if (currentActive is not null &&
                currentActive.EncoderType == newEncoder.EncoderType &&
                currentActive.Id != candidate.PriorEncoderId &&
                !BeatsPriorLoss(newEncoder.InfoNceLoss, currentActive.InfoNceLoss, config.MinImprovement))
            {
                await tx.RollbackAsync(token);
                result = PromotionResult.SupersededByBetterActive;
                return;
            }

            foreach (var row in existingActive)
                row.IsActive = false;

            // Flush the deactivation before the insert so PostgreSQL's filtered unique active
            // index sees an explicit UPDATE-then-INSERT order inside the same transaction.
            if (existingActive.Count > 0)
                await writeCtx.SaveChangesAsync(token);

            newEncoder.IsActive = true;
            writeCtx.Set<MLCpcEncoder>().Add(newEncoder);
            await writeCtx.SaveChangesAsync(token);
            await tx.CommitAsync(token);
        }, ct);

        return result;
    }

    private static bool BeatsPriorLoss(double candidateLoss, double priorLoss, double minImprovement)
    {
        if (!double.IsFinite(candidateLoss) || !double.IsFinite(priorLoss))
            return false;

        return candidateLoss < priorLoss * (1.0 - minImprovement);
    }

    private async Task<TrainOutcome> RecordFailureAsync(
        DbContext writeCtx,
        PairCandidate candidate,
        string reason,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        var key = (candidate.Symbol, candidate.Timeframe, candidate.Regime);
        _consecutiveFailures.TryGetValue(key, out var count);
        count++;
        _consecutiveFailures[key] = count;

        if (count < config.ConsecutiveFailAlertThreshold)
            return TrainOutcome.Rejected;

        var regimeLabel = candidate.Regime?.ToString() ?? "global";
        string dedupeKey = $"MLCpcPretrainer:{candidate.Symbol}:{candidate.Timeframe}:{regimeLabel}";
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var conditionJson = JsonSerializer.Serialize(new
        {
            SchemaVersion = AlertPayloadSchemaVersion,
            Message = $"CPC encoder training failed {count} consecutive cycles for {candidate.Symbol}/{candidate.Timeframe}/{regimeLabel} (reason={reason}).",
            Symbol   = candidate.Symbol,
            Timeframe = candidate.Timeframe.ToString(),
            Regime   = regimeLabel,
            Reason   = reason,
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
            await writeCtx.SaveChangesAsync(ct);
            return TrainOutcome.Rejected;
        }

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType        = AlertType.DataQualityIssue,
            Severity         = AlertSeverity.Medium,
            Symbol           = candidate.Symbol,
            DeduplicationKey = dedupeKey,
            CooldownSeconds  = 3600,
            ConditionJson    = conditionJson,
            LastTriggeredAt  = now,
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
        return TrainOutcome.Rejected;
    }

    private static async Task<List<Candle>> LoadTrainingCandlesAsync(
        DbContext readCtx,
        PairCandidate candidate,
        MLCpcRuntimeConfig config,
        CancellationToken ct)
    {
        int take = candidate.Regime is null
            ? config.TrainingCandles
            : Math.Max(
                config.TrainingCandles,
                Math.Min(
                    config.TrainingCandles * RegimeCandleBackfillMultiplier,
                    config.TrainingCandles + (config.MinCandlesPerRegime * RegimeCandleBackfillMultiplier)));

        var candles = await readCtx.Set<Candle>()
            .AsNoTracking()
            .Where(c => c.Symbol == candidate.Symbol
                     && c.Timeframe == candidate.Timeframe
                     && c.IsClosed
                     && !c.IsDeleted)
            .OrderByDescending(c => c.Timestamp)
            .Take(take)
            .ToListAsync(ct);
        candles.Reverse(); // ascending time order for sequence builder
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
            // Find the latest snapshot whose DetectedAt <= c.Timestamp.
            int idx = Array.BinarySearch(times, c.Timestamp);
            if (idx < 0) idx = ~idx - 1; // last DetectedAt strictly before c.Timestamp
            if (idx < 0) continue;       // candle predates any regime snapshot — skip
            if (regimes[idx] == targetRegime)
                result.Add(c);
        }

        int keep = Math.Max(config.TrainingCandles, config.MinCandlesPerRegime);
        return result.Count > keep
            ? result.Skip(result.Count - keep).ToList()
            : result;
    }

    // ── Supporting types ──────────────────────────────────────────────────────

    private enum TrainOutcome { Promoted, Rejected, Skipped }

    private enum PromotionResult { Promoted, SupersededByBetterActive }

    private sealed record SequenceSplit(
        IReadOnlyList<float[][]> Training,
        IReadOnlyList<float[][]> Validation);

    private sealed record PairCandidate(
        string Symbol,
        Timeframe Timeframe,
        global::LascodiaTradingEngine.Domain.Enums.MarketRegime? Regime,
        long? PriorEncoderId,
        double? PriorInfoNceLoss,
        DateTime? PriorTrainedAt);
}
