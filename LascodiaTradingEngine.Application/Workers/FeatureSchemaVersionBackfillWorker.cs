using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// One-shot migration worker that back-fills <see cref="ModelSnapshot.FeatureSchemaVersion"/>
/// on legacy <see cref="MLModel.ModelBytes"/> JSON blobs serialized before the field existed.
///
/// <para>
/// Runtime inference still handles legacy snapshots via <see cref="ModelSnapshot.ResolveFeatureSchemaVersion"/>,
/// but that fallback intentionally defaults unknown layouts to V1. This worker is stricter: it
/// only persists a schema version when the snapshot carries enough consistent evidence to infer
/// the training-time schema safely. Rows with conflicting, malformed, or insufficient evidence
/// remain unresolved so operators can re-run the migration after repairing or rehydrating them.
/// </para>
///
/// <para>
/// Idempotent: gated by <c>EngineConfig["Migration:FeatureSchemaVersionBackfill:Completed"]</c>.
/// The flag is written as <c>true</c> only when every scanned legacy snapshot is either already
/// tagged or backfilled successfully. Partial runs persist a durable <c>false</c> status with a
/// summary description so future startups retry instead of silently declaring success.
/// </para>
/// </summary>
public sealed class FeatureSchemaVersionBackfillWorker : BackgroundService
{
    internal const string WorkerName = nameof(FeatureSchemaVersionBackfillWorker);

    private const string CompletionFlagKey = "Migration:FeatureSchemaVersionBackfill:Completed";
    private const string CK_BatchSize = "Migration:FeatureSchemaVersionBackfill:BatchSize";
    private const string DistributedLockKey = "workers:feature-schema-version-backfill:cycle";

    private const int DefaultBatchSize = 100;
    private const int MinBatchSize = 1;
    private const int MaxBatchSize = 1000;
    private static readonly TimeSpan MinimumStartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DistributedLockTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<FeatureSchemaVersionBackfillWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private bool _missingDistributedLockWarningEmitted;

    public FeatureSchemaVersionBackfillWorker(
        ILogger<FeatureSchemaVersionBackfillWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "One-shot migration that safely backfills FeatureSchemaVersion on legacy MLModel snapshots and leaves unresolved blobs explicitly incomplete for retry.",
            TimeSpan.FromDays(1));

        bool cycleFaulted = false;
        try
        {
            try
            {
                var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName);
                if (initialDelay < MinimumStartupDelay)
                    initialDelay = MinimumStartupDelay;

                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            long cycleStarted = Stopwatch.GetTimestamp();

            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                var result = await RunCycleAsync(stoppingToken);
                long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;

                _healthMonitor?.RecordBacklogDepth(WorkerName, result.UnresolvedCount);
                _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                _metrics?.WorkerCycleDurationMs.Record(
                    durationMs,
                    new KeyValuePair<string, object?>("worker", WorkerName));
                _metrics?.FeatureSchemaBackfillCycleDurationMs.Record(durationMs);

                if (result.SkippedReason is { Length: > 0 })
                {
                    _logger.LogDebug(
                        "{Worker}: cycle skipped ({Reason}).",
                        WorkerName,
                        result.SkippedReason);
                }
                else if (result.Completed)
                {
                    _logger.LogInformation(
                        "{Worker}: backfill complete. updated={Updated}, legacy={Legacy}, skipped={Skipped}, unresolved={Unresolved}, seen={Seen}.",
                        WorkerName,
                        result.UpdatedCount,
                        result.LegacyCandidateCount,
                        result.SkippedCount,
                        result.UnresolvedCount,
                        result.SeenCount);
                }
                else
                {
                    _logger.LogWarning(
                        "{Worker}: backfill left unresolved rows. updated={Updated}, legacy={Legacy}, skipped={Skipped}, unresolved={Unresolved}, deserFails={DeserFails}, seen={Seen}.",
                        WorkerName,
                        result.UpdatedCount,
                        result.LegacyCandidateCount,
                        result.SkippedCount,
                        result.UnresolvedCount,
                        result.DeserializeFailureCount,
                        result.SeenCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Clean shutdown during backfill; leave completion flag false so next boot retries.
            }
            catch (Exception ex)
            {
                cycleFaulted = true;
                _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName),
                    new KeyValuePair<string, object?>("reason", "feature_schema_backfill_cycle"));
                _logger.LogError(ex, "{Worker}: migration failed — will retry on next startup.", WorkerName);
            }
        }
        finally
        {
            if (cycleFaulted)
                _healthMonitor?.RecordWorkerStopped(WorkerName);
            else
                _healthMonitor?.RecordWorkerCompleted(WorkerName);
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<FeatureSchemaVersionBackfillCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (_distributedLock is null)
        {
            _metrics?.FeatureSchemaBackfillLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate backfill attempts are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, DistributedLockTimeout, ct);
            if (cycleLock is null)
            {
                _metrics?.FeatureSchemaBackfillLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.FeatureSchemaBackfillCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return FeatureSchemaVersionBackfillCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.FeatureSchemaBackfillLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));

            await using (cycleLock)
            {
                return await RunCycleCoreAsync(writeContext, db, settings, ct);
            }
        }

        return await RunCycleCoreAsync(writeContext, db, settings, ct);
    }

    private async Task<FeatureSchemaVersionBackfillCycleResult> RunCycleCoreAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        FeatureSchemaVersionBackfillSettings settings,
        CancellationToken ct)
    {
        var completionFlag = await db.Set<EngineConfig>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == CompletionFlagKey, ct);

        if (completionFlag is not null &&
            !completionFlag.IsDeleted &&
            string.Equals(completionFlag.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            _metrics?.FeatureSchemaBackfillCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "already_completed"));
            return FeatureSchemaVersionBackfillCycleResult.Skipped(settings, "already_completed");
        }

        _logger.LogInformation("{Worker}: starting backfill with batch size {BatchSize}.", WorkerName, settings.BatchSize);

        long updated = 0;
        long skipped = 0;
        long deserializeFailures = 0;
        int unresolved = 0;
        long legacyCandidates = 0;
        long totalSeen = 0;
        long lastId = 0;
        int unresolvedWarnings = 0;
        var unresolvedReasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            var batch = await db.Set<MLModel>()
                .AsNoTracking()
                .Where(model => !model.IsDeleted && model.Id > lastId && model.ModelBytes != null)
                .OrderBy(model => model.Id)
                .Select(model => new BackfillModelRow(model.Id, model.ModelBytes!))
                .Take(settings.BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
                break;

            lastId = batch[^1].Id;
            totalSeen += batch.Count;

            foreach (var model in batch)
            {
                if (ct.IsCancellationRequested)
                    break;

                if (model.ModelBytes.Length == 0)
                {
                    skipped++;
                    continue;
                }

                ModelSnapshot? snapshot;
                try
                {
                    snapshot = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes);
                }
                catch (Exception ex)
                {
                    deserializeFailures++;
                    unresolved++;
                    skipped++;
                    IncrementReason(unresolvedReasonCounts, "deserialize_failed");

                    if (unresolvedWarnings < 5)
                    {
                        unresolvedWarnings++;
                        _logger.LogWarning(
                            ex,
                            "{Worker}: failed to deserialize snapshot for MLModel {Id}; row left unresolved for future retry.",
                            WorkerName,
                            model.Id);
                    }

                    continue;
                }

                if (snapshot is null)
                {
                    skipped++;
                    unresolved++;
                    IncrementReason(unresolvedReasonCounts, "empty_snapshot");

                    if (unresolvedWarnings < 5)
                    {
                        unresolvedWarnings++;
                        _logger.LogWarning(
                            "{Worker}: snapshot payload for MLModel {Id} deserialized to null; row left unresolved for future retry.",
                            WorkerName,
                            model.Id);
                    }

                    continue;
                }

                if (snapshot.FeatureSchemaVersion > 0)
                {
                    skipped++;
                    continue;
                }

                legacyCandidates++;

                if (!TryResolveLegacyFeatureSchemaVersion(snapshot, out int schemaVersion, out string reason))
                {
                    skipped++;
                    unresolved++;
                    IncrementReason(unresolvedReasonCounts, reason);

                    if (unresolvedWarnings < 5)
                    {
                        unresolvedWarnings++;
                        _logger.LogWarning(
                            "{Worker}: could not infer FeatureSchemaVersion for MLModel {Id} ({Reason}); row left unresolved for future retry.",
                            WorkerName,
                            model.Id,
                            reason);
                    }

                    continue;
                }

                snapshot.FeatureSchemaVersion = schemaVersion;
                var updatedBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
                var writeModel = new MLModel { Id = model.Id, ModelBytes = updatedBytes };
                db.Attach(writeModel);
                db.Entry(writeModel).Property(entity => entity.ModelBytes).IsModified = true;
                updated++;
            }

            if (db.ChangeTracker.HasChanges())
            {
                await writeContext.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }

        _metrics?.FeatureSchemaBackfillModelsSeen.Add(totalSeen);

        if (updated > 0)
            _metrics?.FeatureSchemaBackfillModelsUpdated.Add(updated);

        foreach (var (reason, count) in unresolvedReasonCounts)
        {
            _metrics?.FeatureSchemaBackfillModelsUnresolved.Add(
                count,
                new KeyValuePair<string, object?>("reason", reason));
        }

        if (ct.IsCancellationRequested)
        {
            return new FeatureSchemaVersionBackfillCycleResult(
                settings,
                SeenCount: totalSeen,
                LegacyCandidateCount: legacyCandidates,
                UpdatedCount: updated,
                SkippedCount: skipped,
                DeserializeFailureCount: deserializeFailures,
                UnresolvedCount: unresolved,
                Completed: false,
                SkippedReason: "cancelled");
        }

        bool completed = unresolved == 0;
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await UpsertCompletionFlagAsync(
            db,
            completed,
            BuildCompletionDescription(nowUtc, completed, updated, unresolved, deserializeFailures, totalSeen),
            nowUtc,
            ct);
        await writeContext.SaveChangesAsync(ct);

        return new FeatureSchemaVersionBackfillCycleResult(
            settings,
            SeenCount: totalSeen,
            LegacyCandidateCount: legacyCandidates,
            UpdatedCount: updated,
            SkippedCount: skipped,
            DeserializeFailureCount: deserializeFailures,
            UnresolvedCount: unresolved,
            Completed: completed,
            SkippedReason: null);
    }

    private async Task<FeatureSchemaVersionBackfillSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        int configuredBatchSize = DefaultBatchSize;

        var rawBatchSize = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => !config.IsDeleted && config.Key == CK_BatchSize)
            .Select(config => config.Value)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(rawBatchSize) &&
            int.TryParse(rawBatchSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedBatchSize))
        {
            configuredBatchSize = parsedBatchSize;
        }

        int batchSize = Math.Clamp(configuredBatchSize, MinBatchSize, MaxBatchSize);

        if (configuredBatchSize != batchSize)
        {
            _logger.LogDebug(
                "{Worker}: clamped invalid batch size {Configured} to {Effective}.",
                WorkerName,
                configuredBatchSize,
                batchSize);
        }

        return new FeatureSchemaVersionBackfillSettings(batchSize);
    }

    private static bool TryResolveLegacyFeatureSchemaVersion(
        ModelSnapshot snapshot,
        out int schemaVersion,
        out string reason)
    {
        schemaVersion = 0;

        var counts = new HashSet<int>();
        AddEvidence(counts, snapshot.ExpectedInputFeatures);
        AddEvidence(counts, snapshot.ActiveFeatureMask?.Length ?? 0);
        AddEvidence(counts, snapshot.Features?.Length ?? 0);
        AddEvidence(counts, snapshot.Means?.Length ?? 0);
        AddEvidence(counts, snapshot.Stds?.Length ?? 0);

        if (counts.Count == 0)
        {
            reason = "insufficient_evidence";
            return false;
        }

        if (counts.Count > 1)
        {
            reason = "conflicting_evidence";
            return false;
        }

        int featureCount = counts.First();
        if (!TryMapFeatureCountToSchemaVersion(featureCount, out schemaVersion))
        {
            reason = "unknown_feature_count";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryMapFeatureCountToSchemaVersion(int featureCount, out int schemaVersion)
    {
        schemaVersion = featureCount switch
        {
            var count when count == MLFeatureHelper.FeatureCount => 1,
            var count when count == MLFeatureHelper.FeatureCountV2 => 2,
            var count when count == MLFeatureHelper.FeatureCountV3 => 3,
            var count when count == MLFeatureHelper.FeatureCountV4 => 4,
            var count when count == MLFeatureHelper.FeatureCountV5 => 5,
            var count when count == MLFeatureHelper.FeatureCountV6 => 6,
            var count when count == MLFeatureHelper.FeatureCountV7 => 7,
            _ => 0
        };

        return schemaVersion > 0;
    }

    private static void AddEvidence(HashSet<int> counts, int featureCount)
    {
        if (featureCount > 0)
            counts.Add(featureCount);
    }

    private static void IncrementReason(IDictionary<string, int> counts, string reason)
    {
        if (!counts.TryAdd(reason, 1))
            counts[reason]++;
    }

    private async Task UpsertCompletionFlagAsync(
        DbContext db,
        bool completed,
        string description,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var flag = await db.Set<EngineConfig>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(config => config.Key == CompletionFlagKey, ct);

        if (flag is null)
        {
            db.Set<EngineConfig>().Add(new EngineConfig
            {
                Key = CompletionFlagKey,
                Value = completed ? "true" : "false",
                Description = description,
                DataType = ConfigDataType.Bool,
                IsHotReloadable = false,
                LastUpdatedAt = nowUtc,
                IsDeleted = false
            });
            return;
        }

        flag.Value = completed ? "true" : "false";
        flag.Description = description;
        flag.DataType = ConfigDataType.Bool;
        flag.IsHotReloadable = false;
        flag.LastUpdatedAt = nowUtc;
        flag.IsDeleted = false;
    }

    private static string BuildCompletionDescription(
        DateTime completedAtUtc,
        bool completed,
        long updated,
        long unresolved,
        long deserializeFailures,
        long seen)
    {
        string status = completed ? "Completed" : "Partial";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{status} feature-schema backfill at {completedAtUtc:u}; updated={updated}; unresolved={unresolved}; deserFails={deserializeFailures}; seen={seen}.");
    }

    private readonly record struct BackfillModelRow(long Id, byte[] ModelBytes);
}

internal readonly record struct FeatureSchemaVersionBackfillSettings(int BatchSize);

internal readonly record struct FeatureSchemaVersionBackfillCycleResult(
    FeatureSchemaVersionBackfillSettings Settings,
    long SeenCount,
    long LegacyCandidateCount,
    long UpdatedCount,
    long SkippedCount,
    long DeserializeFailureCount,
    int UnresolvedCount,
    bool Completed,
    string? SkippedReason)
{
    public static FeatureSchemaVersionBackfillCycleResult Skipped(
        FeatureSchemaVersionBackfillSettings settings,
        string reason)
        => new(
            settings,
            SeenCount: 0,
            LegacyCandidateCount: 0,
            UpdatedCount: 0,
            SkippedCount: 0,
            DeserializeFailureCount: 0,
            UnresolvedCount: 0,
            Completed: false,
            SkippedReason: reason);
}
