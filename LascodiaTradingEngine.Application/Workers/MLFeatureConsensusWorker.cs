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

    private const string CK_PollSecs                 = "MLFeatureConsensus:PollIntervalSeconds";
    private const string CK_MinModels                = "MLFeatureConsensus:MinModelsForConsensus";
    private const string CK_LockTimeoutSecs          = "MLFeatureConsensus:LockTimeoutSeconds";
    private const string CK_MinSnapshotSpacingSecs   = "MLFeatureConsensus:MinSnapshotSpacingSeconds";
    private const string CK_MaxModelsPerPair         = "MLFeatureConsensus:MaxModelsPerPair";

    private const int DefaultPollSeconds = 3600;
    private const int DefaultMinModels = 3;
    private const int DefaultLockTimeoutSeconds = 0;
    private const int DefaultMinSnapshotSpacingSeconds = 300;
    private const int DefaultMaxModelsPerPair = 128;

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeatureConsensusWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;

    public MLFeatureConsensusWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLFeatureConsensusWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory    = scopeFactory;
        _logger          = logger;
        _distributedLock = distributedLock;
        _healthMonitor   = healthMonitor;
        _metrics         = metrics;
        _timeProvider    = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLFeatureConsensusWorker started.");
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Computes schema-aware feature-importance consensus across active ML models.",
            TimeSpan.FromSeconds(DefaultPollSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSeconds;
            var cycleStart = Stopwatch.GetTimestamp();

            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                pollSecs = await RunCycleAsync(stoppingToken);

                long durationMs = (long)Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
                _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                _metrics?.WorkerCycleDurationMs.Record(
                    durationMs,
                    new KeyValuePair<string, object?>("worker", WorkerName));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName));
                _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                _logger.LogError(ex, "MLFeatureConsensusWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(pollSecs, 60, 86_400)), stoppingToken);
        }

        _healthMonitor?.RecordWorkerStopped(WorkerName);
        _logger.LogInformation("MLFeatureConsensusWorker stopping.");
    }

    internal async Task<int> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readCtx = readDb.GetDbContext();
        var writeCtx = writeDb.GetDbContext();

        var config = await LoadConfigAsync(readCtx, ct);

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics?.MLFeatureConsensusLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));
            _logger.LogWarning(
                "MLFeatureConsensusWorker running without IDistributedLock; duplicate snapshots are possible in multi-instance deployments.");
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
                _logger.LogDebug("MLFeatureConsensusWorker: cycle skipped because distributed lock is held elsewhere.");
                return config.PollSeconds;
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
                await RunCycleCoreAsync(readCtx, writeCtx, config, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }

        return config.PollSeconds;
    }

    private async Task RunCycleCoreAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureConsensusConfig config,
        CancellationToken ct)
    {
        var pairs = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .OrderBy(p => p.Symbol)
            .ThenBy(p => p.Timeframe)
            .ToListAsync(ct);

        _healthMonitor?.RecordBacklogDepth(WorkerName, pairs.Count);
        _logger.LogDebug(
            "Feature consensus cycle: {PairCount} active pair(s), minModels={MinModels}.",
            pairs.Count, config.MinModels);

        int written = 0, skipped = 0;
        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();

            bool didWrite = await ProcessPairAsync(
                readCtx,
                writeCtx,
                pair.Symbol,
                pair.Timeframe,
                config,
                ct);

            if (didWrite) written++;
            else skipped++;
        }

        _logger.LogInformation(
            "MLFeatureConsensusWorker cycle complete: snapshotsWritten={Written}, pairsSkipped={Skipped}, pairsTotal={Total}.",
            written, skipped, pairs.Count);
    }

    private async Task<bool> ProcessPairAsync(
        DbContext readCtx,
        DbContext writeCtx,
        string symbol,
        Timeframe timeframe,
        FeatureConsensusConfig config,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var freshnessCutoff = nowUtc.AddSeconds(-config.MinSnapshotSpacingSeconds);
        bool hasFreshSnapshot = await readCtx.Set<MLFeatureConsensusSnapshot>()
            .AsNoTracking()
            .AnyAsync(s => s.Symbol == symbol
                           && s.Timeframe == timeframe
                           && s.DetectedAt >= freshnessCutoff,
                ct);

        if (hasFreshSnapshot)
        {
            RecordPairSkip("fresh_snapshot", symbol, timeframe);
            return false;
        }

        var models = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                     && !m.IsDeleted
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
            return false;
        }

        var contributors = new List<ConsensusContributor>(models.Count);
        foreach (var model in models)
        {
            var contributor = TryBuildContributor(model, symbol, timeframe);
            if (contributor is not null)
                contributors.Add(contributor);
        }

        if (contributors.Count < config.MinModels)
        {
            RecordPairSkip("insufficient_valid_importance", symbol, timeframe);
            _logger.LogDebug(
                "Pair {Symbol}/{Timeframe}: only {ContributorCount} valid importance contributor(s), need {MinModels}; skipping consensus.",
                symbol, timeframe, contributors.Count, config.MinModels);
            return false;
        }

        var schemaGroup = contributors
            .GroupBy(c => c.SchemaKey)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(c => c.TrainedOn))
            .First();

        if (schemaGroup.Count() < config.MinModels)
        {
            RecordPairSkip("schema_fragmented", symbol, timeframe);
            _logger.LogWarning(
                "Pair {Symbol}/{Timeframe}: no compatible feature schema group has enough contributors. valid={Valid}, largestGroup={Largest}, min={Min}.",
                symbol, timeframe, contributors.Count, schemaGroup.Count(), config.MinModels);
            return false;
        }

        int rejectedSchemaMismatch = contributors.Count - schemaGroup.Count();
        if (rejectedSchemaMismatch > 0)
        {
            RecordModelReject("schema_mismatch", rejectedSchemaMismatch, symbol, timeframe);
            _logger.LogInformation(
                "Pair {Symbol}/{Timeframe}: selected schema {SchemaKey} with {Selected}/{Total} contributor(s); ignored {Ignored} incompatible contributor(s).",
                symbol, timeframe, schemaGroup.Key, schemaGroup.Count(), contributors.Count, rejectedSchemaMismatch);
        }

        var selectedContributors = schemaGroup
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
            return false;
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
            SchemaKey              = schemaGroup.Key,
            FeatureCount           = featureNames.Length,
            ImportanceSourceSummaryJson = sourceSummaryJson,
            ContributorModelIdsJson = contributorIdsJson,
            ContributingModelCount = selectedContributors.Count,
            MeanKendallTau         = Math.Round(meanKendallTau, 6),
            DetectedAt             = nowUtc,
        };

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
            symbol, timeframe, selectedContributors.Count, featureNames.Length, meanKendallTau, schemaGroup.Key);

        return true;
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

        string schemaKey = ResolveSchemaKey(snapshot, extraction.Importance.Keys);
        return new ConsensusContributor(
            model.Id,
            model.LearnerArchitecture.ToString(),
            snapshot.Type,
            schemaKey,
            extraction.Source,
            snapshot.TrainedOn,
            extraction.Importance);
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

    private async Task<FeatureConsensusConfig> LoadConfigAsync(DbContext ctx, CancellationToken ct)
    {
        int pollSeconds = Math.Clamp(
            await GetConfigAsync(ctx, CK_PollSecs, DefaultPollSeconds, ct),
            60,
            86_400);
        int minModels = Math.Clamp(
            await GetConfigAsync(ctx, CK_MinModels, DefaultMinModels, ct),
            2,
            1000);
        int lockTimeoutSeconds = Math.Clamp(
            await GetConfigAsync(ctx, CK_LockTimeoutSecs, DefaultLockTimeoutSeconds, ct),
            0,
            300);
        int minSnapshotSpacingSeconds = Math.Clamp(
            await GetConfigAsync(ctx, CK_MinSnapshotSpacingSecs, DefaultMinSnapshotSpacingSeconds, ct),
            0,
            pollSeconds);
        int maxModelsPerPair = Math.Clamp(
            await GetConfigAsync(ctx, CK_MaxModelsPerPair, DefaultMaxModelsPerPair, ct),
            minModels,
            5000);

        return new FeatureConsensusConfig(
            pollSeconds,
            minModels,
            lockTimeoutSeconds,
            minSnapshotSpacingSeconds,
            maxModelsPerPair);
    }

    private static async Task<T> GetConfigAsync<T>(
        DbContext ctx,
        string key,
        T defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => !c.IsDeleted && c.Key == key, ct);

        if (entry?.Value is null)
            return defaultValue;

        try
        {
            return (T)Convert.ChangeType(entry.Value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    private void RecordPairSkip(string reason, string symbol, Timeframe timeframe)
    {
        _metrics?.MLFeatureConsensusPairsSkipped.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", timeframe.ToString()));
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

    private sealed record FeatureConsensusConfig(
        int PollSeconds,
        int MinModels,
        int LockTimeoutSeconds,
        int MinSnapshotSpacingSeconds,
        int MaxModelsPerPair);

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
