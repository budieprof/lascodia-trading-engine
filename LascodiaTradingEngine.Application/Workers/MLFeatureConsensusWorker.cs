using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes cross-architecture feature importance consensus for each active (symbol, timeframe) pair.
/// </summary>
public sealed class MLFeatureConsensusWorker : BackgroundService
{
    private const string WorkerName = nameof(MLFeatureConsensusWorker);
    private const string DistributedLockKey = "ml:feature-consensus:cycle";

    private const string CK_Enabled                  = "MLFeatureConsensus:Enabled";
    private const string CK_InitialDelaySecs         = "MLFeatureConsensus:InitialDelaySeconds";
    private const string CK_PollSecs                 = "MLFeatureConsensus:PollIntervalSeconds";
    private const string CK_MinModels                = "MLFeatureConsensus:MinModelsForConsensus";
    private const string CK_MinArchitectures         = "MLFeatureConsensus:MinArchitecturesForConsensus";
    private const string CK_LockTimeoutSecs          = "MLFeatureConsensus:LockTimeoutSeconds";
    private const string CK_MinSnapshotSpacingSecs   = "MLFeatureConsensus:MinSnapshotSpacingSeconds";
    private const string CK_MaxModelsPerPair         = "MLFeatureConsensus:MaxModelsPerPair";
    private const string CK_MaxPairsPerCycle         = "MLFeatureConsensus:MaxPairsPerCycle";
    private const string CK_DbCommandTimeoutSecs     = "MLFeatureConsensus:DbCommandTimeoutSeconds";

    private const int DefaultPollSeconds = 3600;
    private const int DefaultMinModels = 3;
    private const int DefaultMinArchitectures = 2;
    private const int DefaultLockTimeoutSeconds = 0;
    private const int DefaultMinSnapshotSpacingSeconds = 300;
    private const int DefaultMaxModelsPerPair = 128;
    private const int DefaultMaxPairsPerCycle = 1000;
    private const int DefaultDbCommandTimeoutSeconds = 30;
    private const double MinImportanceMass = 1.0e-12;

    private static readonly string[] ConfigKeys =
    [
        CK_Enabled,
        CK_InitialDelaySecs,
        CK_PollSecs,
        CK_MinModels,
        CK_MinArchitectures,
        CK_LockTimeoutSecs,
        CK_MinSnapshotSpacingSecs,
        CK_MaxModelsPerPair,
        CK_MaxPairsPerCycle,
        CK_DbCommandTimeoutSecs
    ];

    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeatureConsensusWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly MLFeatureConsensusOptions _options;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    public MLFeatureConsensusWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLFeatureConsensusWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        MLFeatureConsensusOptions? options = null)
    {
        _scopeFactory    = scopeFactory;
        _logger          = logger;
        _distributedLock = distributedLock;
        _healthMonitor   = healthMonitor;
        _metrics         = metrics;
        _timeProvider    = timeProvider ?? TimeProvider.System;
        _options         = options ?? new MLFeatureConsensusOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureConsensusWorker started.");
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Computes schema-aware feature-importance consensus across active ML models.",
            TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds)));

        DateTime lastCycleStartUtc = DateTime.MinValue;
        DateTime lastSuccessUtc = DateTime.MinValue;
        TimeSpan currentPollInterval = TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds));

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName)
                               + TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(_options.InitialDelaySeconds));
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                if (lastSuccessUtc != DateTime.MinValue)
                    _metrics?.MLFeatureConsensusTimeSinceLastSuccessSec.Record((nowUtc - lastSuccessUtc).TotalSeconds);

                if (nowUtc - lastCycleStartUtc >= currentPollInterval)
                {
                    lastCycleStartUtc = nowUtc;
                    var cycleStart = Stopwatch.GetTimestamp();

                    try
                    {
                        _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                        var result = await RunCycleAsync(stoppingToken);
                        currentPollInterval = result.Config.PollInterval;

                        long durationMs = (long)Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
                        _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                        _metrics?.WorkerCycleDurationMs.Record(
                            durationMs,
                            new KeyValuePair<string, object?>("worker", WorkerName));
                        _metrics?.MLFeatureConsensusCycleDurationMs.Record(durationMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug(
                                "{Worker}: cycle skipped ({Reason}).",
                                WorkerName,
                                result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: pairs={Pairs}, written={Written}, skipped={Skipped}, rejectedModels={Rejected}.",
                                WorkerName,
                                result.CandidatePairCount,
                                result.SnapshotsWritten,
                                result.PairsSkipped,
                                result.ModelRejects);
                        }

                        var previousFailures = ConsecutiveCycleFailures;
                        if (previousFailures > 0)
                        {
                            _healthMonitor?.RecordRecovery(WorkerName, previousFailures);
                            _logger.LogInformation(
                                "{Worker}: recovered after {Failures} consecutive failure(s).",
                                WorkerName,
                                previousFailures);
                        }

                        ConsecutiveCycleFailures = 0;
                        lastSuccessUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _consecutiveCycleFailuresField);
                        _metrics?.WorkerErrors.Add(
                            1,
                            new KeyValuePair<string, object?>("worker", WorkerName),
                            new KeyValuePair<string, object?>("reason", "ml_feature_consensus_cycle"));
                        _healthMonitor?.RecordRetry(WorkerName);
                        _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                        _logger.LogError(ex, "MLFeatureConsensusWorker loop error.");
                    }
                }

                var delay = ConsecutiveCycleFailures > 0
                    ? CalculateBackoffDelay(ConsecutiveCycleFailures)
                    : WakeInterval;
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("MLFeatureConsensusWorker stopping.");
        }
    }

    internal async Task<FeatureConsensusCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readCtx = readDb.GetDbContext();
        var writeCtx = writeDb.GetDbContext();

        var config = await LoadConfigAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
        {
            RecordCycleSkipped("disabled");
            return FeatureConsensusCycleResult.Skipped(config, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics?.MLFeatureConsensusLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "MLFeatureConsensusWorker running without IDistributedLock; duplicate snapshots are possible in multi-instance deployments.");
            }
        }
        else
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(config.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _metrics?.MLFeatureConsensusLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                _logger.LogDebug("MLFeatureConsensusWorker: cycle skipped because distributed lock is held elsewhere.");
                return FeatureConsensusCycleResult.Skipped(config, "lock_busy");
            }

            _metrics?.MLFeatureConsensusLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RunCycleCoreAsync(readCtx, writeCtx, config, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    internal async Task<FeatureConsensusCycleResult> RunConsensusAsync(
        DbContext readCtx,
        DbContext writeCtx,
        CancellationToken ct)
    {
        var config = await LoadConfigAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
            return FeatureConsensusCycleResult.Skipped(config, "disabled");

        return await RunCycleCoreAsync(readCtx, writeCtx, config, ct);
    }

    private async Task<FeatureConsensusCycleResult> RunCycleCoreAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureConsensusConfig config,
        CancellationToken ct)
    {
        var activeModelQuery = readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && (m.Status == MLModelStatus.Active || m.IsFallbackChampion)
                        && !m.IsSuppressed
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer
                        && m.ModelBytes != null);

        var pairQuery = activeModelQuery
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct();

        var pairs = await pairQuery
            .OrderBy(p => p.Symbol)
            .ThenBy(p => p.Timeframe)
            .Take(config.MaxPairsPerCycle + 1)
            .ToListAsync(ct);

        var truncated = pairs.Count > config.MaxPairsPerCycle;
        var skippedByLimit = 0;
        if (truncated)
        {
            pairs.RemoveAt(pairs.Count - 1);
            var totalPairs = await pairQuery.CountAsync(ct);
            skippedByLimit = Math.Max(0, totalPairs - config.MaxPairsPerCycle);
            if (skippedByLimit > 0)
                RecordPairSkip("cycle_limit", skippedByLimit);
        }

        _healthMonitor?.RecordBacklogDepth(WorkerName, pairs.Count + skippedByLimit);
        _logger.LogDebug(
            "Feature consensus cycle: {PairCount} active pair(s), minModels={MinModels}.",
            pairs.Count + skippedByLimit, config.MinModels);

        int written = 0, skipped = skippedByLimit, failed = 0, rejectedModels = 0, contributors = 0;
        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();

            FeatureConsensusPairResult pairResult;
            try
            {
                pairResult = await ProcessPairAsync(
                    readCtx,
                    writeCtx,
                    pair.Symbol,
                    pair.Timeframe,
                    config,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                skipped++;
                RecordPairSkip("pair_error", pair.Symbol, pair.Timeframe);
                _logger.LogWarning(
                    ex,
                    "MLFeatureConsensusWorker: failed pair {Symbol}/{Timeframe}; continuing.",
                    pair.Symbol,
                    pair.Timeframe);
                continue;
            }

            rejectedModels += pairResult.ModelRejects;
            contributors += pairResult.Contributors;

            if (pairResult.Written)
                written++;
            else
                skipped++;
        }

        _logger.LogInformation(
            "MLFeatureConsensusWorker cycle complete: snapshotsWritten={Written}, pairsSkipped={Skipped}, pairsFailed={Failed}, pairsTotal={Total}.",
            written, skipped, failed, pairs.Count + skippedByLimit);

        return new FeatureConsensusCycleResult(
            config,
            CandidatePairCount: pairs.Count + skippedByLimit,
            SnapshotsWritten: written,
            PairsSkipped: skipped,
            PairFailures: failed,
            ModelRejects: rejectedModels,
            Contributors: contributors,
            SkippedReason: null);
    }

    private async Task<FeatureConsensusPairResult> ProcessPairAsync(
        DbContext readCtx,
        DbContext writeCtx,
        string symbol,
        Timeframe timeframe,
        FeatureConsensusConfig config,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var freshnessCutoff = nowUtc.AddSeconds(-config.MinSnapshotSpacingSeconds);
        bool hasFreshSnapshot = await HasFreshSnapshotAsync(readCtx, symbol, timeframe, freshnessCutoff, ct);

        if (hasFreshSnapshot)
        {
            RecordPairSkip("fresh_snapshot", symbol, timeframe);
            return FeatureConsensusPairResult.Skipped("fresh_snapshot");
        }

        var models = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                     && !m.IsDeleted
                     && (m.Status == MLModelStatus.Active || m.IsFallbackChampion)
                     && !m.IsSuppressed
                     && !m.IsMetaLearner
                     && !m.IsMamlInitializer
                     && m.ModelBytes != null
                     && m.Symbol == symbol
                     && m.Timeframe == timeframe)
            .OrderByDescending(m => m.TrainedAt)
            .ThenByDescending(m => m.Id)
            .Take(config.MaxModelsPerPair)
            .ToListAsync(ct);

        if (models.Count < config.MinModels)
        {
            RecordPairSkip("insufficient_models", symbol, timeframe);
            _logger.LogDebug(
                "Pair {Symbol}/{Timeframe}: only {ModelCount} active model(s), need {MinModels}; skipping consensus.",
                symbol, timeframe, models.Count, config.MinModels);
            return FeatureConsensusPairResult.Skipped("insufficient_models");
        }

        var contributors = new List<ConsensusContributor>(models.Count);
        var rejectedModels = 0;
        foreach (var model in models)
        {
            var contributor = TryBuildContributor(model, symbol, timeframe);
            if (contributor is not null)
            {
                contributors.Add(contributor);
            }
            else
            {
                rejectedModels++;
            }
        }

        if (contributors.Count < config.MinModels)
        {
            RecordPairSkip("insufficient_valid_importance", symbol, timeframe);
            _logger.LogDebug(
                "Pair {Symbol}/{Timeframe}: only {ContributorCount} valid importance contributor(s), need {MinModels}; skipping consensus.",
                symbol, timeframe, contributors.Count, config.MinModels);
            return FeatureConsensusPairResult.Skipped("insufficient_valid_importance", rejectedModels);
        }

        var schemaGroups = contributors
            .GroupBy(c => c.SchemaKey)
            .Select(g => new
            {
                SchemaKey = g.Key,
                Contributors = g.ToList(),
                ArchitectureCount = g.Select(c => c.Architecture).Distinct(StringComparer.Ordinal).Count(),
                LatestTrainedOn = g.Max(c => c.TrainedOn),
            })
            .OrderByDescending(g => g.Contributors.Count)
            .ThenByDescending(g => g.ArchitectureCount)
            .ThenByDescending(g => g.LatestTrainedOn)
            .ToList();

        var schemaGroup = schemaGroups
            .Where(g => g.Contributors.Count >= config.MinModels
                        && g.ArchitectureCount >= config.MinArchitectures)
            .OrderByDescending(g => g.ArchitectureCount)
            .ThenByDescending(g => g.Contributors.Count)
            .ThenByDescending(g => g.LatestTrainedOn)
            .FirstOrDefault();

        if (schemaGroup is null)
        {
            var largestGroup = schemaGroups.First();
            var reason = largestGroup.Contributors.Count < config.MinModels
                ? "schema_fragmented"
                : "insufficient_architecture_diversity";
            RecordPairSkip(reason, symbol, timeframe);
            _logger.LogWarning(
                "Pair {Symbol}/{Timeframe}: no compatible feature-schema group met consensus requirements. valid={Valid}, largestGroup={Largest}, largestArchitectures={Architectures}, minModels={MinModels}, minArchitectures={MinArchitectures}.",
                symbol,
                timeframe,
                contributors.Count,
                largestGroup.Contributors.Count,
                largestGroup.ArchitectureCount,
                config.MinModels,
                config.MinArchitectures);
            return FeatureConsensusPairResult.Skipped(reason, rejectedModels);
        }

        int rejectedSchemaMismatch = contributors.Count - schemaGroup.Contributors.Count;
        if (rejectedSchemaMismatch > 0)
        {
            RecordModelReject("schema_mismatch", rejectedSchemaMismatch, symbol, timeframe);
            rejectedModels += rejectedSchemaMismatch;
            _logger.LogInformation(
                "Pair {Symbol}/{Timeframe}: selected schema {SchemaKey} with {Selected}/{Total} contributor(s); ignored {Ignored} incompatible contributor(s).",
                symbol, timeframe, schemaGroup.SchemaKey, schemaGroup.Contributors.Count, contributors.Count, rejectedSchemaMismatch);
        }

        var selectedContributors = schemaGroup.Contributors
            .OrderBy(c => c.ModelId)
            .ToList();

        var featureNames = selectedContributors
            .SelectMany(c => c.Importance.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (featureNames.Length == 0)
        {
            RecordPairSkip("no_common_features", symbol, timeframe);
            return FeatureConsensusPairResult.Skipped("no_common_features", rejectedModels);
        }

        var consensusEntries = BuildConsensusEntries(selectedContributors, featureNames);
        double meanKendallTau = ComputeMeanKendallTau(selectedContributors, featureNames);
        string consensusJson = JsonSerializer.Serialize(consensusEntries, JsonOptions);
        string sourceSummaryJson = JsonSerializer.Serialize(
            selectedContributors
                .GroupBy(c => c.ImportanceSource, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            JsonOptions);
        string contributorIdsJson = JsonSerializer.Serialize(
            selectedContributors.Select(c => c.ModelId).Order().ToArray(),
            JsonOptions);

        var snapshot = new MLFeatureConsensusSnapshot
        {
            Symbol                 = symbol,
            Timeframe              = timeframe,
            FeatureConsensusJson   = consensusJson,
            SchemaKey              = schemaGroup.SchemaKey,
            FeatureCount           = featureNames.Length,
            ImportanceSourceSummaryJson = sourceSummaryJson,
            ContributorModelIdsJson = contributorIdsJson,
            ContributingModelCount = selectedContributors.Count,
            MeanKendallTau         = Math.Round(meanKendallTau, 6),
            DetectedAt             = nowUtc,
        };

        var latestCutoff = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-config.MinSnapshotSpacingSeconds);
        if (await HasFreshSnapshotAsync(writeCtx, symbol, timeframe, latestCutoff, ct))
        {
            RecordPairSkip("fresh_snapshot_race", symbol, timeframe);
            return FeatureConsensusPairResult.Skipped("fresh_snapshot_race", rejectedModels);
        }

        writeCtx.Set<MLFeatureConsensusSnapshot>().Add(snapshot);
        await writeCtx.SaveChangesAsync(ct);

        _metrics?.MLFeatureConsensusSnapshots.Add(
            1,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", timeframe.ToString()));
        _metrics?.MLFeatureConsensusContributors.Record(
            selectedContributors.Count,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", timeframe.ToString()));
        _metrics?.MLFeatureConsensusMeanKendallTau.Record(
            meanKendallTau,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", timeframe.ToString()));

        _logger.LogInformation(
            "Feature consensus computed for {Symbol}/{Timeframe}: contributors={ContributorCount}, features={FeatureCount}, meanKendallTau={Tau:F4}, schema={SchemaKey}.",
            symbol, timeframe, selectedContributors.Count, featureNames.Length, meanKendallTau, schemaGroup.SchemaKey);

        return FeatureConsensusPairResult.Wrote(selectedContributors.Count, rejectedModels);
    }

    private ConsensusContributor? TryBuildContributor(MLModel model, string symbol, Timeframe timeframe)
    {
        ModelSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!, JsonOptions);
        }
        catch (Exception ex)
        {
            RecordModelReject("deserialize_failed", 1, symbol, timeframe);
            _logger.LogDebug(
                ex,
                "Pair {Symbol}/{Timeframe}: failed to deserialize model {ModelId}; skipping.",
                symbol, timeframe, model.Id);
            return null;
        }

        if (snapshot is null)
        {
            RecordModelReject("empty_snapshot", 1, symbol, timeframe);
            return null;
        }

        var extraction = ModelSnapshotFeatureImportanceExtractor.Extract(snapshot);
        if (extraction.InvalidValueCount > 0)
        {
            RecordModelReject("invalid_importance_value", extraction.InvalidValueCount, symbol, timeframe);
            _logger.LogDebug(
                "Pair {Symbol}/{Timeframe}: model {ModelId} had {Rejected} invalid feature importance value(s).",
                symbol, timeframe, model.Id, extraction.InvalidValueCount);
        }

        if (extraction.Importance.Count == 0)
        {
            RecordModelReject("no_importance", 1, symbol, timeframe);
            return null;
        }

        var normalizedImportance = NormalizeImportance(extraction.Importance);
        if (normalizedImportance.Count == 0)
        {
            RecordModelReject("zero_importance", 1, symbol, timeframe);
            return null;
        }

        string schemaKey = ResolveSchemaKey(snapshot, normalizedImportance.Keys);
        return new ConsensusContributor(
            model.Id,
            model.LearnerArchitecture.ToString(),
            snapshot.Type,
            schemaKey,
            extraction.Source,
            snapshot.TrainedOn,
            normalizedImportance);
    }

    private static IReadOnlyDictionary<string, double> NormalizeImportance(
        IReadOnlyDictionary<string, double> importance)
    {
        var cleaned = new Dictionary<string, double>(StringComparer.Ordinal);
        double total = 0.0;

        foreach (var (feature, rawValue) in importance)
        {
            if (string.IsNullOrWhiteSpace(feature) || !double.IsFinite(rawValue))
                continue;

            var value = Math.Abs(rawValue);
            if (value <= 0.0)
                continue;

            cleaned[feature] = value;
            total += value;
        }

        if (total <= MinImportanceMass)
            return new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var feature in cleaned.Keys.ToArray())
            cleaned[feature] /= total;

        return cleaned;
    }

    private static List<FeatureConsensusEntry> BuildConsensusEntries(
        IReadOnlyList<ConsensusContributor> contributors,
        IReadOnlyList<string> featureNames)
    {
        var entries = new List<FeatureConsensusEntry>(featureNames.Count);

        foreach (string featureName in featureNames)
        {
            var values = new double[contributors.Count];
            for (int i = 0; i < contributors.Count; i++)
                values[i] = contributors[i].Importance.TryGetValue(featureName, out double v) ? v : 0.0;

            double meanImportance = values.Average();
            double stdImportance = ComputeStdDev(values);
            double agreementScore = meanImportance > 1e-12
                ? Math.Clamp(1.0 - (stdImportance / meanImportance), 0.0, 1.0)
                : 0.0;

            entries.Add(new FeatureConsensusEntry
            {
                Feature        = featureName,
                MeanImportance = Math.Round(meanImportance, 6),
                StdImportance  = Math.Round(stdImportance, 6),
                AgreementScore = Math.Round(agreementScore, 4),
            });
        }

        return entries
            .OrderByDescending(e => e.MeanImportance)
            .ThenBy(e => e.Feature, StringComparer.Ordinal)
            .ToList();
    }

    private static double ComputeStdDev(double[] values)
    {
        if (values.Length <= 1) return 0.0;

        double mean = values.Average();
        double variance = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            double diff = values[i] - mean;
            variance += diff * diff;
        }

        return Math.Sqrt(variance / values.Length);
    }

    private static double ComputeMeanKendallTau(
        IReadOnlyList<ConsensusContributor> contributors,
        IReadOnlyList<string> featureNames)
    {
        if (contributors.Count < 2 || featureNames.Count < 2)
            return 0.0;

        double sum = 0.0;
        int pairs = 0;

        for (int i = 0; i < contributors.Count; i++)
        {
            for (int j = i + 1; j < contributors.Count; j++)
            {
                sum += KendallTauB(
                    BuildVector(contributors[i], featureNames),
                    BuildVector(contributors[j], featureNames));
                pairs++;
            }
        }

        return pairs > 0 ? sum / pairs : 0.0;
    }

    private static double[] BuildVector(
        ConsensusContributor contributor,
        IReadOnlyList<string> featureNames)
    {
        var vector = new double[featureNames.Count];
        for (int i = 0; i < featureNames.Count; i++)
            vector[i] = contributor.Importance.TryGetValue(featureNames[i], out double v) ? v : 0.0;
        return vector;
    }

    private static double KendallTauB(double[] x, double[] y)
    {
        int n = x.Length;
        if (n < 2 || y.Length != n)
            return 0.0;

        long concordant = 0;
        long discordant = 0;
        long tiesX = 0;
        long tiesY = 0;

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                int xCmp = x[i].CompareTo(x[j]);
                int yCmp = y[i].CompareTo(y[j]);

                if (xCmp == 0 && yCmp == 0)
                    continue;
                if (xCmp == 0)
                    tiesX++;
                else if (yCmp == 0)
                    tiesY++;
                else if (xCmp == yCmp)
                    concordant++;
                else
                    discordant++;
            }
        }

        double left = concordant + discordant + tiesX;
        double right = concordant + discordant + tiesY;
        double denominator = Math.Sqrt(left * right);
        return denominator > 0.0
            ? (concordant - discordant) / denominator
            : 0.0;
    }

    private static string ResolveSchemaKey(ModelSnapshot snapshot, IEnumerable<string> featureNames)
    {
        var names = featureNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        if (names.Length == 0)
            return $"schema-v{snapshot.ResolveFeatureSchemaVersion()}:empty";

        var payload = string.Join('|', names);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        if (!string.IsNullOrWhiteSpace(snapshot.FeatureSchemaFingerprint))
            return $"schema-fp:{snapshot.FeatureSchemaFingerprint}:importance:{names.Length}:{hash[..16]}";

        return $"schema-v{snapshot.ResolveFeatureSchemaVersion()}:importance:{names.Length}:{hash[..16]}";
    }

    internal static async Task<FeatureConsensusConfig> LoadConfigAsync(
        DbContext ctx,
        MLFeatureConsensusOptions options,
        CancellationToken ct)
    {
        var rows = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => ConfigKeys.Contains(c.Key) && !c.IsDeleted)
            .Select(c => new { c.Id, c.Key, c.Value, c.LastUpdatedAt })
            .ToListAsync(ct);

        var values = rows
            .Where(c => c.Value is not null)
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(c => c.LastUpdatedAt)
                    .ThenBy(c => c.Id)
                    .Last().Value!,
                StringComparer.Ordinal);

        int pollSeconds = NormalizePollSeconds(GetConfig(values, CK_PollSecs, options.PollIntervalSeconds));
        int minModels = NormalizeMinModels(GetConfig(values, CK_MinModels, options.MinModelsForConsensus));
        int minArchitectures = NormalizeMinArchitectures(
            GetConfig(values, CK_MinArchitectures, options.MinArchitecturesForConsensus),
            minModels);
        int maxModelsPerPair = NormalizeMaxModelsPerPair(
            GetConfig(values, CK_MaxModelsPerPair, options.MaxModelsPerPair),
            minModels);

        return new FeatureConsensusConfig(
            Enabled: GetConfig(values, CK_Enabled, options.Enabled),
            InitialDelay: TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(
                GetConfig(values, CK_InitialDelaySecs, options.InitialDelaySeconds))),
            PollInterval: TimeSpan.FromSeconds(pollSeconds),
            PollSeconds: pollSeconds,
            MinModels: minModels,
            MinArchitectures: minArchitectures,
            LockTimeoutSeconds: NormalizeLockTimeoutSeconds(
                GetConfig(values, CK_LockTimeoutSecs, options.LockTimeoutSeconds)),
            MinSnapshotSpacingSeconds: NormalizeMinSnapshotSpacingSeconds(
                GetConfig(values, CK_MinSnapshotSpacingSecs, options.MinSnapshotSpacingSeconds),
                pollSeconds),
            MaxModelsPerPair: maxModelsPerPair,
            MaxPairsPerCycle: NormalizeMaxPairsPerCycle(
                GetConfig(values, CK_MaxPairsPerCycle, options.MaxPairsPerCycle)),
            DbCommandTimeoutSeconds: NormalizeDbCommandTimeoutSeconds(
                GetConfig(values, CK_DbCommandTimeoutSecs, options.DbCommandTimeoutSeconds)));
    }

    private static T GetConfig<T>(
        IReadOnlyDictionary<string, string> values,
        string key,
        T defaultValue)
    {
        if (!values.TryGetValue(key, out var raw))
            return defaultValue;

        return TryConvertConfig(raw, out T parsed)
            ? parsed
            : defaultValue;
    }

    private static bool TryConvertConfig<T>(string value, out T result)
    {
        object? parsed = null;
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        var normalized = value.Trim();

        if (targetType == typeof(string))
        {
            parsed = value;
        }
        else if (targetType == typeof(int)
                 && int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            parsed = intValue;
        }
        else if (targetType == typeof(bool)
                 && TryParseBool(normalized, out var boolValue))
        {
            parsed = boolValue;
        }

        if (parsed is T typed)
        {
            result = typed;
            return true;
        }

        result = default!;
        return false;
    }

    internal static int NormalizeInitialDelaySeconds(int value)
        => value is >= 0 and <= 86_400 ? value : 0;

    internal static int NormalizePollSeconds(int value)
        => value is >= 60 and <= 86_400 ? value : DefaultPollSeconds;

    internal static int NormalizeMinModels(int value)
        => value is >= 2 and <= 1000 ? value : DefaultMinModels;

    internal static int NormalizeMinArchitectures(int value, int minModels)
    {
        if (value < 1 || value > 100)
            return Math.Min(DefaultMinArchitectures, minModels);

        return Math.Min(value, minModels);
    }

    internal static int NormalizeLockTimeoutSeconds(int value)
        => value is >= 0 and <= 300 ? value : DefaultLockTimeoutSeconds;

    internal static int NormalizeMinSnapshotSpacingSeconds(int value, int pollSeconds)
        => value >= 0 ? Math.Min(value, pollSeconds) : DefaultMinSnapshotSpacingSeconds;

    internal static int NormalizeMaxModelsPerPair(int value, int minModels)
        => value is >= 2 and <= 5000 ? Math.Max(value, minModels) : Math.Max(DefaultMaxModelsPerPair, minModels);

    internal static int NormalizeMaxPairsPerCycle(int value)
        => value is >= 1 and <= 100_000 ? value : DefaultMaxPairsPerCycle;

    internal static int NormalizeDbCommandTimeoutSeconds(int value)
        => value is >= 1 and <= 600 ? value : DefaultDbCommandTimeoutSeconds;

    private static bool TryParseBool(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;

        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        return false;
    }

    private void RecordPairSkip(string reason, string symbol, Timeframe timeframe)
    {
        _metrics?.MLFeatureConsensusPairsSkipped.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", timeframe.ToString()));
    }

    private void RecordPairSkip(string reason, string symbol, Timeframe timeframe, int count)
    {
        if (count <= 0)
            return;

        _metrics?.MLFeatureConsensusPairsSkipped.Add(
            count,
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", timeframe.ToString()));
    }

    private void RecordPairSkip(string reason, int count)
    {
        if (count <= 0)
            return;

        _metrics?.MLFeatureConsensusPairsSkipped.Add(
            count,
            new KeyValuePair<string, object?>("reason", reason));
    }

    private void RecordModelReject(string reason, int count, string symbol, Timeframe timeframe)
    {
        if (count <= 0)
            return;

        _metrics?.MLFeatureConsensusModelRejects.Add(
            count,
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", timeframe.ToString()));
    }

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLFeatureConsensusCyclesSkipped.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason));

    private static async Task<bool> HasFreshSnapshotAsync(
        DbContext ctx,
        string symbol,
        Timeframe timeframe,
        DateTime freshnessCutoff,
        CancellationToken ct)
    {
        if (freshnessCutoff <= DateTime.MinValue)
            return false;

        return await ctx.Set<MLFeatureConsensusSnapshot>()
            .AsNoTracking()
            .AnyAsync(s => s.Symbol == symbol
                           && s.Timeframe == timeframe
                           && s.DetectedAt >= freshnessCutoff,
                ct);
    }

    private static TimeSpan CalculateBackoffDelay(int consecutiveFailures)
    {
        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var seconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
    }

    private static void ApplyCommandTimeout(DbContext db, int seconds)
    {
        try
        {
            if (db.Database.IsRelational())
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(seconds));
        }
        catch (InvalidOperationException)
        {
            // Some providers do not expose relational command timeout configuration.
        }
    }

    internal sealed record FeatureConsensusConfig(
        bool Enabled,
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int PollSeconds,
        int MinModels,
        int MinArchitectures,
        int LockTimeoutSeconds,
        int MinSnapshotSpacingSeconds,
        int MaxModelsPerPair,
        int MaxPairsPerCycle,
        int DbCommandTimeoutSeconds);

    internal sealed record FeatureConsensusCycleResult(
        FeatureConsensusConfig Config,
        int CandidatePairCount,
        int SnapshotsWritten,
        int PairsSkipped,
        int PairFailures,
        int ModelRejects,
        int Contributors,
        string? SkippedReason)
    {
        public static FeatureConsensusCycleResult Skipped(FeatureConsensusConfig config, string reason)
            => new(config, 0, 0, 0, 0, 0, 0, reason);
    }

    private sealed record FeatureConsensusPairResult(
        bool Written,
        int Contributors,
        int ModelRejects,
        string? SkipReason)
    {
        public static FeatureConsensusPairResult Wrote(int contributors, int modelRejects)
            => new(true, contributors, modelRejects, null);

        public static FeatureConsensusPairResult Skipped(string reason, int modelRejects = 0)
            => new(false, 0, modelRejects, reason);
    }

    private sealed record ConsensusContributor(
        long ModelId,
        string Architecture,
        string SnapshotType,
        string SchemaKey,
        string ImportanceSource,
        DateTime TrainedOn,
        IReadOnlyDictionary<string, double> Importance);

    private sealed class FeatureConsensusEntry
    {
        public string Feature { get; set; } = string.Empty;
        public double MeanImportance { get; set; }
        public double StdImportance { get; set; }
        public double AgreementScore { get; set; }
    }
}
