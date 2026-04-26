using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Aggregates multi-horizon direction accuracy for each routable active ML model using
/// the <c>HorizonCorrect3</c>, <c>HorizonCorrect6</c>, and <c>HorizonCorrect12</c>
/// fields on <see cref="MLModelPredictionLog"/>, and persists the results as
/// <see cref="MLModelHorizonAccuracy"/> rows.
/// </summary>
public sealed class MLHorizonAccuracyWorker : BackgroundService
{
    private const int AlertCooldownSeconds = 3600;
    private const int MaxAlertDestinationLength = 100;
    private const string WorkerName = nameof(MLHorizonAccuracyWorker);
    private const string DistributedLockKey = "ml:horizon-accuracy:cycle";
    private const string ConfigPrefixUpper = "MLHORIZON:";
    private const string HorizonAccuracyGapReason = "horizon_accuracy_gap";
    private const string HorizonAccuracyUniqueIndex = "IX_MLModelHorizonAccuracy_MLModelId_HorizonBars";
    private const string AlertDeduplicationIndex = "IX_Alert_DeduplicationKey";
    private const string AlertDedupPrefix = "MLHorizon:";

    // EngineConfig keys. Values are read live each cycle, with appsettings-backed
    // MLHorizonAccuracyOptions as validated defaults.
    private const string CK_Enabled = "MLHorizon:Enabled";
    private const string CK_PollSecs = "MLHorizon:PollIntervalSeconds";
    private const string CK_Window = "MLHorizon:WindowDays";
    private const string CK_MinPreds = "MLHorizon:MinPredictions";
    private const string CK_GapThr = "MLHorizon:HorizonGapThreshold";
    private const string CK_WilsonZ = "MLHorizon:WilsonZ";
    private const string CK_AlertDest = "MLHorizon:AlertDestination";
    private const string CK_MaxModelsPerCycle = "MLHorizon:MaxModelsPerCycle";
    private const string CK_LockTimeoutSecs = "MLHorizon:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSeconds = "MLHorizon:DbCommandTimeoutSeconds";

    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The three forward-look horizons tracked in <see cref="MLModelPredictionLog"/>.
    /// </summary>
    private static readonly (int Bars, string Field)[] Horizons =
    [
        (3, "HorizonCorrect3"),
        (6, "HorizonCorrect6"),
        (12, "HorizonCorrect12"),
    ];

    private static readonly int[] SupportedHorizonBars = Horizons.Select(h => h.Bars).ToArray();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLHorizonAccuracyWorker> _logger;
    private readonly IDatabaseExceptionClassifier? _dbExceptionClassifier;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly MLHorizonAccuracyOptions _options;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    public MLHorizonAccuracyWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLHorizonAccuracyWorker> logger,
        IDatabaseExceptionClassifier? dbExceptionClassifier = null,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        MLHorizonAccuracyOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _dbExceptionClassifier = dbExceptionClassifier;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new MLHorizonAccuracyOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Computes active-model multi-horizon accuracy profiles and shallow-edge alerts.",
            TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds)));

        int initialDelaySeconds = NormalizeInitialDelaySeconds(_options.InitialDelaySeconds);
        if (initialDelaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(initialDelaySeconds), stoppingToken);

        DateTime? lastSuccessUtc = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = NormalizePollSeconds(_options.PollIntervalSeconds);
            var cycleStart = Stopwatch.GetTimestamp();

            try
            {
                _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                if (lastSuccessUtc is not null)
                {
                    _metrics?.MLHorizonAccuracyTimeSinceLastSuccessSec.Record(
                        (_timeProvider.GetUtcNow().UtcDateTime - lastSuccessUtc.Value).TotalSeconds);
                }

                pollSecs = await RunCycleAsync(stoppingToken);

                long durationMs = (long)Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
                _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                _metrics?.WorkerCycleDurationMs.Record(durationMs, Tag("worker", WorkerName));
                _metrics?.MLHorizonAccuracyCycleDurationMs.Record(durationMs);
                ConsecutiveCycleFailures = 0;
                lastSuccessUtc = _timeProvider.GetUtcNow().UtcDateTime;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ConsecutiveCycleFailures++;
                _metrics?.WorkerErrors.Add(1, Tag("worker", WorkerName));
                _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                _logger.LogError(ex, "{Worker} loop error", WorkerName);
            }

            var delay = ConsecutiveCycleFailures > 0
                ? ComputeRetryDelay(ConsecutiveCycleFailures)
                : TimeSpan.FromSeconds(pollSecs);

            try
            {
                await DelayInChunksAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _healthMonitor?.RecordWorkerStopped(WorkerName);
        _logger.LogInformation("{Worker} stopping.", WorkerName);
    }

    internal async Task<int> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
        var config = await LoadConfigAsync(readCtx, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
        {
            RecordCycleSkipped("disabled");
            _logger.LogDebug("{Worker}: cycle skipped because the worker is disabled.", WorkerName);
            return config.PollSeconds;
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is not null)
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(config.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _metrics?.MLHorizonAccuracyLockAttempts.Add(1, Tag("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                _logger.LogDebug("{Worker}: cycle skipped because distributed lock is held elsewhere.", WorkerName);
                return config.PollSeconds;
            }

            _metrics?.MLHorizonAccuracyLockAttempts.Add(1, Tag("outcome", "acquired"));
        }
        else
        {
            _metrics?.MLHorizonAccuracyLockAttempts.Add(1, Tag("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate work is possible in multi-instance deployments.",
                    WorkerName);
            }
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                await ComputeAllModelsAsync(readCtx, writeCtx, config, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }

        return config.PollSeconds;
    }

    internal async Task<HorizonCycleStats> ComputeAllModelsAsync(
        DbContext readCtx,
        DbContext writeCtx,
        CancellationToken ct)
    {
        var config = await LoadConfigAsync(readCtx, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);
        return await ComputeAllModelsAsync(readCtx, writeCtx, config, ct);
    }

    private async Task<HorizonCycleStats> ComputeAllModelsAsync(
        DbContext readCtx,
        DbContext writeCtx,
        HorizonAccuracyConfig config,
        CancellationToken ct)
    {
        var stats = new HorizonCycleStats();

        if (!config.Enabled)
        {
            RecordCycleSkipped("disabled");
            return stats;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var windowStart = now.AddDays(-config.WindowDays);
        var selection = await LoadActiveModelSelectionAsync(readCtx, config, ct);
        stats.ModelsSkipped += selection.InvalidModelCount + selection.CapacitySkippedCount;

        if (selection.InvalidModelCount > 0)
            _metrics?.MLHorizonAccuracyModelsSkipped.Add(selection.InvalidModelCount, Tag("reason", "invalid_model"));
        if (selection.CapacitySkippedCount > 0)
            _metrics?.MLHorizonAccuracyModelsSkipped.Add(selection.CapacitySkippedCount, Tag("reason", "max_models_per_cycle"));

        _healthMonitor?.RecordBacklogDepth(WorkerName, selection.SelectedModels.Count);

        stats.RowsSoftDeleted += await ReconcileAccuracyRowsAsync(writeCtx, selection.ActiveModelIds, ct);
        stats.AlertsResolved += await ResolveAlertsForInactiveModelsAsync(writeCtx, selection.ActiveModelIds, now, ct);
        writeCtx.ChangeTracker.Clear();

        if (selection.SelectedModels.Count == 0)
        {
            RecordCycleSkipped("no_active_models");
            _logger.LogDebug("{Worker}: no active routable ML models to evaluate.", WorkerName);
        }

        foreach (var model in selection.SelectedModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ComputeForModelAsync(
                    model.Id,
                    model.Symbol,
                    model.Timeframe,
                    windowStart,
                    now,
                    config,
                    readCtx,
                    writeCtx,
                    stats,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stats.ModelsFailed++;
                _metrics?.MLHorizonAccuracyModelsSkipped.Add(1, Tag("reason", "failed"));
                _logger.LogWarning(ex,
                    "HorizonAccuracy: compute failed for model {Id} ({Symbol}/{Tf}); skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
            finally
            {
                writeCtx.ChangeTracker.Clear();
            }
        }

        _metrics?.MLHorizonAccuracyModelsEvaluated.Add(stats.ModelsEvaluated);
        if (stats.RowsWritten > 0)
            _metrics?.MLHorizonAccuracyRowsWritten.Add(stats.RowsWritten);
        if (stats.RowsSoftDeleted > 0)
            _metrics?.MLHorizonAccuracyRowsSoftDeleted.Add(stats.RowsSoftDeleted);
        if (stats.AlertsRaised > 0)
            _metrics?.MLHorizonAccuracyAlertTransitions.Add(stats.AlertsRaised, Tag("transition", "dispatched"));
        if (stats.AlertsRefreshed > 0)
            _metrics?.MLHorizonAccuracyAlertTransitions.Add(stats.AlertsRefreshed, Tag("transition", "refreshed"));
        if (stats.AlertsResolved > 0)
            _metrics?.MLHorizonAccuracyAlertTransitions.Add(stats.AlertsResolved, Tag("transition", "resolved"));

        _logger.LogInformation(
            "{Worker} cycle complete: evaluated={Evaluated}, failed={Failed}, skipped={Skipped}, rowsWritten={RowsWritten}, rowsSoftDeleted={RowsSoftDeleted}, alertsRaised={AlertsRaised}, alertsResolved={AlertsResolved}.",
            WorkerName,
            stats.ModelsEvaluated,
            stats.ModelsFailed,
            stats.ModelsSkipped,
            stats.RowsWritten,
            stats.RowsSoftDeleted,
            stats.AlertsRaised,
            stats.AlertsResolved);

        return stats;
    }

    private async Task ComputeForModelAsync(
        long modelId,
        string symbol,
        Timeframe timeframe,
        DateTime windowStart,
        DateTime computedAt,
        HorizonAccuracyConfig config,
        DbContext readCtx,
        DbContext writeCtx,
        HorizonCycleStats stats,
        CancellationToken ct)
    {
        var logs = readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId == modelId
                     && l.ModelRole == ModelRole.Champion
                     && l.PredictedAt >= windowStart
                     && !l.IsDeleted)
            .AsNoTracking();

        var aggregate = await LoadAggregateStatsAsync(logs, ct);

        var primary = aggregate.Primary;
        int primaryTotal = primary.Total;
        int primaryCorrect = primary.Correct;
        double primaryAcc = primaryTotal > 0 ? (double)primaryCorrect / primaryTotal : 0.0;
        bool primaryReliable = primaryTotal >= config.MinPredictions;

        foreach (var (horizonBars, _) in Horizons)
        {
            var horizon = aggregate.ForHorizon(horizonBars);

            int total = horizon.Total;
            int correct = horizon.Correct;
            double accuracy = total > 0 ? (double)correct / total : 0.0;
            double lowerBound = WilsonLowerBound(correct, total, config.WilsonZ);
            double primaryGap = primaryReliable ? Math.Max(0.0, primaryAcc - accuracy) : 0.0;
            bool isReliable = primaryReliable && total >= config.MinPredictions;
            string status = isReliable
                ? "Computed"
                : total < config.MinPredictions
                    ? "InsufficientHorizonSamples"
                    : "InsufficientPrimarySamples";

            await UpsertHorizonAccuracyAsync(
                writeCtx,
                modelId,
                symbol,
                timeframe,
                horizonBars,
                total,
                correct,
                accuracy,
                lowerBound,
                primaryTotal,
                primaryCorrect,
                primaryAcc,
                primaryGap,
                isReliable,
                status,
                windowStart,
                computedAt,
                _dbExceptionClassifier,
                ct);

            stats.RowsWritten++;
            _metrics?.MLHorizonAccuracySamples.Record(
                total,
                Tag("symbol", symbol),
                Tag("timeframe", timeframe.ToString()),
                Tag("horizon", horizonBars));
            _metrics?.MLHorizonAccuracyPrimaryGap.Record(
                primaryGap,
                Tag("symbol", symbol),
                Tag("timeframe", timeframe.ToString()),
                Tag("horizon", horizonBars));

            _logger.LogDebug(
                "HorizonAccuracy: model {Id} ({Symbol}/{Tf}) h={H}bar - acc={Acc:P1} lb={Lb:P1} n={N} status={Status}",
                modelId, symbol, timeframe, horizonBars, accuracy, lowerBound, total, status);

            if (horizonBars == 3)
            {
                var transition = await SyncHorizonGapAlertAsync(
                    writeCtx,
                    modelId,
                    symbol,
                    timeframe,
                    primaryAcc,
                    accuracy,
                    lowerBound,
                    primaryGap,
                    config.HorizonGapThreshold,
                    config.AlertDestination,
                    total,
                    primaryTotal,
                    isReliable,
                    computedAt,
                    ct);

                stats.RecordAlertTransition(transition);
            }
        }

        stats.ModelsEvaluated++;
    }

    private async Task<ModelSelection> LoadActiveModelSelectionAsync(
        DbContext readCtx,
        HorizonAccuracyConfig config,
        CancellationToken ct)
    {
        var rawModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive
                     && !m.IsDeleted
                     && !m.IsSuppressed
                     && !m.IsMetaLearner
                     && !m.IsMamlInitializer
                     && (m.Status == MLModelStatus.Active || m.IsFallbackChampion))
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);

        var validModels = rawModels
            .Select(m => new ActiveModelInfo(m.Id, NormalizeSymbol(m.Symbol), m.Timeframe, null))
            .Where(m => !string.IsNullOrWhiteSpace(m.Symbol))
            .ToList();

        var lastComputedByModel = await LoadLastComputedByModelAsync(
            readCtx,
            validModels.Select(m => m.Id).ToArray(),
            ct);

        var modelsWithFreshness = validModels
            .Select(m => m with
            {
                LastComputedAt = lastComputedByModel.TryGetValue(m.Id, out var computedAt)
                    ? computedAt
                    : null
            })
            .ToList();

        var selected = modelsWithFreshness
            .OrderBy(m => m.LastComputedAt ?? DateTime.MinValue)
            .ThenBy(m => m.Symbol, StringComparer.Ordinal)
            .ThenBy(m => m.Timeframe)
            .ThenBy(m => m.Id)
            .Take(config.MaxModelsPerCycle)
            .ToList();

        int invalidModelCount = rawModels.Count - validModels.Count;
        int capacitySkippedCount = Math.Max(0, validModels.Count - selected.Count);
        return new ModelSelection(
            validModels.Select(m => m.Id).ToArray(),
            selected,
            invalidModelCount,
            capacitySkippedCount);
    }

    private static async Task<Dictionary<long, DateTime>> LoadLastComputedByModelAsync(
        DbContext readCtx,
        IReadOnlyCollection<long> activeModelIds,
        CancellationToken ct)
    {
        if (activeModelIds.Count == 0)
            return [];

        var ids = activeModelIds.ToArray();
        var rows = await readCtx.Set<MLModelHorizonAccuracy>()
            .AsNoTracking()
            .Where(r => ids.Contains(r.MLModelId))
            .Select(r => new { r.MLModelId, r.HorizonBars, r.ComputedAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.MLModelId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => r.HorizonBars).Distinct().Count() < Horizons.Length
                    ? DateTime.MinValue
                    : g.Min(r => r.ComputedAt));
    }

    private static async Task<int> ReconcileAccuracyRowsAsync(
        DbContext writeCtx,
        IReadOnlyCollection<long> activeModelIds,
        CancellationToken ct)
    {
        var activeIdSet = activeModelIds.ToHashSet();
        var supportedHorizons = SupportedHorizonBars.ToHashSet();
        var rows = await writeCtx.Set<MLModelHorizonAccuracy>()
            .Where(r => !r.IsDeleted)
            .ToListAsync(ct);

        int softDeleted = 0;
        foreach (var row in rows)
        {
            if (activeIdSet.Contains(row.MLModelId) && supportedHorizons.Contains(row.HorizonBars))
                continue;

            row.IsDeleted = true;
            softDeleted++;
        }

        foreach (var duplicate in rows
            .Where(r => !r.IsDeleted)
            .GroupBy(r => new { r.MLModelId, r.HorizonBars })
            .SelectMany(g => g
                .OrderByDescending(r => r.ComputedAt)
                .ThenByDescending(r => r.Id)
                .Skip(1)))
        {
            duplicate.IsDeleted = true;
            softDeleted++;
        }

        if (softDeleted > 0)
            await writeCtx.SaveChangesAsync(ct);

        return softDeleted;
    }

    private async Task<int> ResolveAlertsForInactiveModelsAsync(
        DbContext writeCtx,
        IReadOnlyCollection<long> activeModelIds,
        DateTime resolvedAt,
        CancellationToken ct)
    {
        var activeIdSet = activeModelIds.ToHashSet();
        var alerts = await writeCtx.Set<Alert>()
            .Where(a => a.IsActive
                     && !a.IsDeleted
                     && a.DeduplicationKey != null
                     && a.DeduplicationKey.StartsWith(AlertDedupPrefix))
            .ToListAsync(ct);

        int resolved = 0;
        foreach (var alert in alerts)
        {
            if (TryParseModelIdFromDedupKey(alert.DeduplicationKey, out long modelId)
                && activeIdSet.Contains(modelId))
            {
                continue;
            }

            alert.IsActive = false;
            alert.AutoResolvedAt = resolvedAt;
            resolved++;
        }

        if (resolved > 0)
        {
            await writeCtx.SaveChangesAsync(ct);
            _logger.LogInformation(
                "HorizonAccuracy: resolved {Count} stale horizon gap alert(s) for inactive or invalid models.",
                resolved);
        }

        return resolved;
    }

    private static async Task<HorizonAggregateStats> LoadAggregateStatsAsync(
        IQueryable<MLModelPredictionLog> logs,
        CancellationToken ct)
    {
        var row = await logs
            .GroupBy(_ => 1)
            .Select(g => new
            {
                PrimaryTotal = g.Count(l => l.DirectionCorrect != null),
                PrimaryCorrect = g.Count(l => l.DirectionCorrect == true),
                H3Total = g.Count(l => l.HorizonCorrect3 != null),
                H3Correct = g.Count(l => l.HorizonCorrect3 == true),
                H6Total = g.Count(l => l.HorizonCorrect6 != null),
                H6Correct = g.Count(l => l.HorizonCorrect6 == true),
                H12Total = g.Count(l => l.HorizonCorrect12 != null),
                H12Correct = g.Count(l => l.HorizonCorrect12 == true),
            })
            .SingleOrDefaultAsync(ct);

        return row is null
            ? HorizonAggregateStats.Empty
            : new HorizonAggregateStats(
                new OutcomeStats(row.PrimaryTotal, row.PrimaryCorrect),
                new OutcomeStats(row.H3Total, row.H3Correct),
                new OutcomeStats(row.H6Total, row.H6Correct),
                new OutcomeStats(row.H12Total, row.H12Correct));
    }

    private static async Task UpsertHorizonAccuracyAsync(
        DbContext writeCtx,
        long modelId,
        string symbol,
        Timeframe timeframe,
        int horizonBars,
        int total,
        int correct,
        double accuracy,
        double lowerBound,
        int primaryTotal,
        int primaryCorrect,
        double primaryAccuracy,
        double primaryGap,
        bool isReliable,
        string status,
        DateTime windowStart,
        DateTime computedAt,
        IDatabaseExceptionClassifier? dbExceptionClassifier,
        CancellationToken ct)
    {
        int rows = await UpdateHorizonAccuracyAsync(
            writeCtx,
            modelId,
            symbol,
            timeframe,
            horizonBars,
            total,
            correct,
            accuracy,
            lowerBound,
            primaryTotal,
            primaryCorrect,
            primaryAccuracy,
            primaryGap,
            isReliable,
            status,
            windowStart,
            computedAt,
            ct);

        if (rows > 0) return;

        var row = new MLModelHorizonAccuracy
        {
            MLModelId = modelId,
            Symbol = symbol,
            Timeframe = timeframe,
            HorizonBars = horizonBars,
            TotalPredictions = total,
            CorrectPredictions = correct,
            Accuracy = accuracy,
            AccuracyLowerBound = lowerBound,
            PrimaryTotalPredictions = primaryTotal,
            PrimaryCorrectPredictions = primaryCorrect,
            PrimaryAccuracy = primaryAccuracy,
            PrimaryAccuracyGap = primaryGap,
            IsReliable = isReliable,
            Status = status,
            WindowStart = windowStart,
            ComputedAt = computedAt,
        };

        writeCtx.Set<MLModelHorizonAccuracy>().Add(row);

        try
        {
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsExpectedUniqueConstraintViolation(
                   ex,
                   HorizonAccuracyUniqueIndex,
                   dbExceptionClassifier,
                   "MLModelHorizonAccuracy",
                   "MLModelId",
                   "HorizonBars"))
        {
            Detach(writeCtx, row);

            rows = await UpdateHorizonAccuracyAsync(
                writeCtx,
                modelId,
                symbol,
                timeframe,
                horizonBars,
                total,
                correct,
                accuracy,
                lowerBound,
                primaryTotal,
                primaryCorrect,
                primaryAccuracy,
                primaryGap,
                isReliable,
                status,
                windowStart,
                computedAt,
                ct);

            if (rows > 0) return;
            throw;
        }
    }

    private static Task<int> UpdateHorizonAccuracyAsync(
        DbContext writeCtx,
        long modelId,
        string symbol,
        Timeframe timeframe,
        int horizonBars,
        int total,
        int correct,
        double accuracy,
        double lowerBound,
        int primaryTotal,
        int primaryCorrect,
        double primaryAccuracy,
        double primaryGap,
        bool isReliable,
        string status,
        DateTime windowStart,
        DateTime computedAt,
        CancellationToken ct)
        => writeCtx.Set<MLModelHorizonAccuracy>()
            .Where(r => r.MLModelId == modelId && r.HorizonBars == horizonBars)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Symbol, symbol)
                .SetProperty(r => r.Timeframe, timeframe)
                .SetProperty(r => r.TotalPredictions, total)
                .SetProperty(r => r.CorrectPredictions, correct)
                .SetProperty(r => r.Accuracy, accuracy)
                .SetProperty(r => r.AccuracyLowerBound, lowerBound)
                .SetProperty(r => r.PrimaryTotalPredictions, primaryTotal)
                .SetProperty(r => r.PrimaryCorrectPredictions, primaryCorrect)
                .SetProperty(r => r.PrimaryAccuracy, primaryAccuracy)
                .SetProperty(r => r.PrimaryAccuracyGap, primaryGap)
                .SetProperty(r => r.IsReliable, isReliable)
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.WindowStart, windowStart)
                .SetProperty(r => r.ComputedAt, computedAt),
                ct);

    private async Task<AlertTransition> SyncHorizonGapAlertAsync(
        DbContext writeCtx,
        long modelId,
        string symbol,
        Timeframe timeframe,
        double primaryAccuracy,
        double horizon3Accuracy,
        double horizon3LowerBound,
        double gap,
        double horizonGapThreshold,
        string alertDestination,
        int sampleCount,
        int primarySampleCount,
        bool isReliable,
        DateTime computedAt,
        CancellationToken ct)
    {
        string dedupKey = HorizonGapDedupKey(modelId, symbol, timeframe);
        bool isBreached = isReliable && gap > horizonGapThreshold;

        if (!isBreached)
        {
            int resolved = await writeCtx.Set<Alert>()
                .Where(a => a.DeduplicationKey == dedupKey
                         && a.IsActive
                         && !a.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.IsActive, false)
                    .SetProperty(a => a.AutoResolvedAt, computedAt),
                    ct);

            if (resolved > 0)
            {
                _logger.LogInformation(
                    "HorizonAccuracy: resolved horizon gap alert for model {Id} ({Symbol}/{Tf}).",
                    modelId, symbol, timeframe);
                return AlertTransition.Resolved;
            }

            return AlertTransition.None;
        }

        string conditionJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            reason = HorizonAccuracyGapReason,
            severity = "warning",
            symbol,
            timeframe = timeframe.ToString(),
            modelId,
            primaryDirectionAcc = primaryAccuracy,
            horizon3BarAcc = horizon3Accuracy,
            horizon3BarLowerBound = horizon3LowerBound,
            gap,
            horizonGapThreshold,
            alertDestination,
            sampleCount,
            primarySampleCount,
            computedAt,
        });

        var transition = await RefreshExistingHorizonGapAlertAsync(
            writeCtx,
            dedupKey,
            symbol,
            conditionJson,
            ct);

        if (transition is not AlertTransition.None)
            return transition;

        _logger.LogWarning(
            "HorizonAccuracy: model {Id} ({Symbol}/{Tf}) - primary={P:P1} h3={H3:P1} gap={Gap:P1} exceeds threshold {Thr:P0}.",
            modelId,
            symbol,
            timeframe,
            primaryAccuracy,
            horizon3Accuracy,
            gap,
            horizonGapThreshold);

        var alert = new Alert
        {
            AlertType = AlertType.MLModelDegraded,
            Symbol = symbol,
            ConditionJson = conditionJson,
            Severity = AlertSeverity.Medium,
            DeduplicationKey = dedupKey,
            CooldownSeconds = AlertCooldownSeconds,
            IsActive = true,
        };

        writeCtx.Set<Alert>().Add(alert);

        try
        {
            await writeCtx.SaveChangesAsync(ct);
            return AlertTransition.Dispatched;
        }
        catch (DbUpdateException ex) when (IsExpectedUniqueConstraintViolation(
                   ex,
                   AlertDeduplicationIndex,
                   _dbExceptionClassifier,
                   "Alert",
                   "DeduplicationKey"))
        {
            Detach(writeCtx, alert);
            return await RefreshExistingHorizonGapAlertAsync(
                writeCtx,
                dedupKey,
                symbol,
                conditionJson,
                ct);
        }
    }

    private static async Task<AlertTransition> RefreshExistingHorizonGapAlertAsync(
        DbContext writeCtx,
        string dedupKey,
        string symbol,
        string conditionJson,
        CancellationToken ct)
    {
        var existingAlerts = await writeCtx.Set<Alert>()
            .Where(a => a.DeduplicationKey == dedupKey && !a.IsDeleted)
            .OrderByDescending(a => a.IsActive)
            .ThenByDescending(a => a.Id)
            .ToListAsync(ct);

        if (existingAlerts.Count == 0)
            return AlertTransition.None;

        var alert = existingAlerts[0];
        bool wasActive = alert.IsActive;

        alert.AlertType = AlertType.MLModelDegraded;
        alert.Symbol = symbol;
        alert.ConditionJson = conditionJson;
        alert.Severity = AlertSeverity.Medium;
        alert.CooldownSeconds = AlertCooldownSeconds;
        alert.AutoResolvedAt = null;
        alert.IsActive = true;

        foreach (var duplicate in existingAlerts.Skip(1))
            duplicate.IsDeleted = true;

        await writeCtx.SaveChangesAsync(ct);
        return wasActive ? AlertTransition.Refreshed : AlertTransition.Dispatched;
    }

    private async Task<HorizonAccuracyConfig> LoadConfigAsync(DbContext ctx, CancellationToken ct)
    {
        var rows = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.ToUpper().StartsWith(ConfigPrefixUpper))
            .Select(c => new { c.Id, c.Key, c.Value, c.LastUpdatedAt })
            .ToListAsync(ct);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.OrderBy(r => r.LastUpdatedAt).ThenBy(r => r.Id))
        {
            if (!string.IsNullOrWhiteSpace(row.Key))
                values[row.Key.Trim()] = row.Value ?? string.Empty;
        }

        return new HorizonAccuracyConfig(
            Enabled: GetBool(values, CK_Enabled, _options.Enabled),
            PollSeconds: NormalizePollSeconds(GetInt(values, CK_PollSecs, _options.PollIntervalSeconds)),
            WindowDays: NormalizeWindowDays(GetInt(values, CK_Window, _options.WindowDays)),
            MinPredictions: NormalizeMinPredictions(GetInt(values, CK_MinPreds, _options.MinPredictions)),
            HorizonGapThreshold: NormalizeProbability(GetDouble(values, CK_GapThr, _options.HorizonGapThreshold), 0.10),
            WilsonZ: NormalizeWilsonZ(GetDouble(values, CK_WilsonZ, _options.WilsonZ)),
            AlertDestination: NormalizeDestination(GetString(values, CK_AlertDest, _options.AlertDestination)),
            MaxModelsPerCycle: NormalizeMaxModelsPerCycle(GetInt(values, CK_MaxModelsPerCycle, _options.MaxModelsPerCycle)),
            LockTimeoutSeconds: NormalizeLockTimeoutSeconds(GetInt(values, CK_LockTimeoutSecs, _options.LockTimeoutSeconds)),
            DbCommandTimeoutSeconds: NormalizeCommandTimeoutSeconds(GetInt(values, CK_DbCommandTimeoutSeconds, _options.DbCommandTimeoutSeconds)));
    }

    internal static double WilsonLowerBound(int successes, int total, double z)
    {
        if (total <= 0) return 0.0;

        successes = Math.Clamp(successes, 0, total);
        z = NormalizeWilsonZ(z);

        double p = (double)successes / total;
        double z2 = z * z;
        double denominator = 1.0 + z2 / total;
        double centre = p + z2 / (2.0 * total);
        double margin = z * Math.Sqrt((p * (1.0 - p) + z2 / (4.0 * total)) / total);
        return Math.Clamp((centre - margin) / denominator, 0.0, 1.0);
    }

    internal static int NormalizePollSeconds(int value)
        => value is >= 1 and <= 86_400 ? value : 3600;

    internal static int NormalizeWindowDays(int value)
        => value is >= 1 and <= 3650 ? value : 30;

    internal static int NormalizeMinPredictions(int value)
        => value is >= 1 and <= 1_000_000 ? value : 20;

    internal static double NormalizeProbability(double value, double defaultValue)
        => double.IsFinite(value) && value >= 0.0 && value <= 1.0 ? value : defaultValue;

    internal static double NormalizeWilsonZ(double value)
        => double.IsFinite(value) && value >= 0.0 && value <= 5.0 ? value : 1.96;

    internal static string NormalizeDestination(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "ml-ops";

        var trimmed = value.Trim();
        return trimmed.Length <= MaxAlertDestinationLength
            ? trimmed
            : trimmed[..MaxAlertDestinationLength];
    }

    private static int NormalizeInitialDelaySeconds(int value)
        => value is >= 0 and <= 86_400 ? value : 0;

    private static int NormalizeMaxModelsPerCycle(int value)
        => value is >= 1 and <= 100_000 ? value : 512;

    private static int NormalizeLockTimeoutSeconds(int value)
        => value is >= 0 and <= 300 ? value : 0;

    private static int NormalizeCommandTimeoutSeconds(int value)
        => value is >= 1 and <= 600 ? value : 30;

    private static int GetInt(Dictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out var raw)
           && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static double GetDouble(Dictionary<string, string> values, string key, double fallback)
        => values.TryGetValue(key, out var raw)
           && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var raw) && bool.TryParse(raw, out bool parsed)
            ? parsed
            : fallback;

    private static string GetString(Dictionary<string, string> values, string key, string? fallback)
        => values.TryGetValue(key, out var raw) ? raw : fallback ?? string.Empty;

    private static void ApplyCommandTimeout(DbContext db, int seconds)
    {
        if (db.Database.IsRelational())
            db.Database.SetCommandTimeout(seconds);
    }

    private static TimeSpan ComputeRetryDelay(int consecutiveFailures)
    {
        double seconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(0, consecutiveFailures - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
    }

    private static async Task DelayInChunksAsync(TimeSpan delay, CancellationToken ct)
    {
        var remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            var next = remaining < WakeInterval ? remaining : WakeInterval;
            await Task.Delay(next, ct);
            remaining -= next;
        }
    }

    private static string HorizonGapDedupKey(long modelId, string symbol, Timeframe timeframe)
        => $"{AlertDedupPrefix}{modelId}:{symbol}:{timeframe}:3";

    private static string NormalizeSymbol(string? symbol)
        => string.IsNullOrWhiteSpace(symbol) ? string.Empty : symbol.Trim().ToUpperInvariant();

    private static bool TryParseModelIdFromDedupKey(string? dedupKey, out long modelId)
    {
        modelId = 0;
        if (string.IsNullOrWhiteSpace(dedupKey)
            || !dedupKey.StartsWith(AlertDedupPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = dedupKey.AsSpan(AlertDedupPrefix.Length);
        int separator = remainder.IndexOf(':');
        return separator > 0
               && long.TryParse(
                   remainder[..separator],
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out modelId);
    }

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLHorizonAccuracyCyclesSkipped.Add(1, Tag("reason", reason));

    private static KeyValuePair<string, object?> Tag(string name, object? value)
        => new(name, value);

    internal static bool IsExpectedUniqueConstraintViolation(
        DbUpdateException ex,
        string expectedConstraintName,
        IDatabaseExceptionClassifier? dbExceptionClassifier = null,
        params string[] requiredMessageTokens)
    {
        ArgumentNullException.ThrowIfNull(ex);

        bool isUnique = dbExceptionClassifier?.IsUniqueConstraintViolation(ex) == true
                        || LooksLikeUniqueConstraintViolation(ex);

        if (!isUnique) return false;

        string? constraintName = TryGetProviderConstraintName(ex);
        if (!string.IsNullOrWhiteSpace(constraintName))
        {
            return string.Equals(
                constraintName,
                expectedConstraintName,
                StringComparison.OrdinalIgnoreCase);
        }

        string message = FlattenExceptionMessages(ex);
        if (message.Contains(expectedConstraintName, StringComparison.OrdinalIgnoreCase))
            return true;

        return requiredMessageTokens.Length > 0 &&
               requiredMessageTokens.All(token =>
                   message.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeUniqueConstraintViolation(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
            if (string.Equals(sqlState, "23505", StringComparison.Ordinal))
                return true;

            var sqliteErrorCode = current.GetType().GetProperty("SqliteErrorCode")?.GetValue(current);
            var sqliteExtendedErrorCode = current.GetType().GetProperty("SqliteExtendedErrorCode")?.GetValue(current);

            if (sqliteErrorCode is int code && code == 19)
                return true;

            if (sqliteExtendedErrorCode is int extendedCode && extendedCode == 2067)
                return true;

            string message = current.Message ?? string.Empty;
            if (message.Contains("23505", StringComparison.Ordinal) ||
                message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? TryGetProviderConstraintName(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            var constraintName = current.GetType().GetProperty("ConstraintName")?.GetValue(current) as string;
            if (!string.IsNullOrWhiteSpace(constraintName))
                return constraintName;
        }

        return null;
    }

    private static string FlattenExceptionMessages(Exception ex)
    {
        var messages = new List<string>();

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
                messages.Add(current.Message);
        }

        return string.Join(' ', messages);
    }

    private static void Detach<TEntity>(
        DbContext ctx,
        TEntity entity)
        where TEntity : class
    {
        var entry = ctx.Entry(entity);
        if (entry.State != EntityState.Detached)
            entry.State = EntityState.Detached;
    }

    internal sealed class HorizonCycleStats
    {
        public int ModelsEvaluated { get; set; }
        public int ModelsFailed { get; set; }
        public int ModelsSkipped { get; set; }
        public int RowsWritten { get; set; }
        public int RowsSoftDeleted { get; set; }
        public int AlertsRaised { get; set; }
        public int AlertsRefreshed { get; set; }
        public int AlertsResolved { get; set; }

        internal void RecordAlertTransition(AlertTransition transition)
        {
            if (transition == AlertTransition.Dispatched) AlertsRaised++;
            else if (transition == AlertTransition.Refreshed) AlertsRefreshed++;
            else if (transition == AlertTransition.Resolved) AlertsResolved++;
        }
    }

    private readonly record struct OutcomeStats(int Total, int Correct);

    private readonly record struct HorizonAggregateStats(
        OutcomeStats Primary,
        OutcomeStats H3,
        OutcomeStats H6,
        OutcomeStats H12)
    {
        public static HorizonAggregateStats Empty { get; } = new(
            new OutcomeStats(0, 0),
            new OutcomeStats(0, 0),
            new OutcomeStats(0, 0),
            new OutcomeStats(0, 0));

        public OutcomeStats ForHorizon(int horizonBars)
            => horizonBars switch
            {
                3 => H3,
                6 => H6,
                12 => H12,
                _ => throw new ArgumentOutOfRangeException(nameof(horizonBars), horizonBars, "Unsupported horizon."),
            };
    }

    private sealed record HorizonAccuracyConfig(
        bool Enabled,
        int PollSeconds,
        int WindowDays,
        int MinPredictions,
        double HorizonGapThreshold,
        double WilsonZ,
        string AlertDestination,
        int MaxModelsPerCycle,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds);

    private readonly record struct ActiveModelInfo(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        DateTime? LastComputedAt);

    private sealed record ModelSelection(
        IReadOnlyCollection<long> ActiveModelIds,
        IReadOnlyList<ActiveModelInfo> SelectedModels,
        int InvalidModelCount,
        int CapacitySkippedCount);

    internal enum AlertTransition
    {
        None,
        Dispatched,
        Refreshed,
        Resolved,
    }
}
