using System.Diagnostics;
using System.Globalization;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Exports rolling live ML model metrics into <see cref="EngineConfig"/> for dashboards,
/// operations tooling, and expiry-managed metric blocks.
/// </summary>
public sealed class MLMetricsExportWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLMetricsExportWorker);

    private const string DistributedLockKey = "workers:ml-metrics-export:cycle";
    private const string ModelMetricsPrefix = "MLMetrics:Model:";

    private const string CK_Enabled = "MLMetrics:Enabled";
    private const string CK_InitialDelaySeconds = "MLMetrics:InitialDelaySeconds";
    private const string CK_PollSecs = "MLMetrics:PollIntervalSeconds";
    private const string CK_WindowDays = "MLMetrics:WindowDays";
    private const string CK_MinResolvedSamples = "MLMetrics:MinResolvedSamples";
    private const string CK_MaxModelsPerCycle = "MLMetrics:MaxModelsPerCycle";
    private const string CK_MaxPredictionLogsPerModel = "MLMetrics:MaxPredictionLogsPerModel";
    private const string CK_WriteLegacyAliases = "MLMetrics:WriteLegacySymbolTimeframeAliases";
    private const string CK_LockTimeoutSeconds = "MLMetrics:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSeconds = "MLMetrics:DbCommandTimeoutSeconds";

    private static readonly string[] ConfigKeys =
    [
        CK_Enabled,
        CK_InitialDelaySeconds,
        CK_PollSecs,
        CK_WindowDays,
        CK_MinResolvedSamples,
        CK_MaxModelsPerCycle,
        CK_MaxPredictionLogsPerModel,
        CK_WriteLegacyAliases,
        CK_LockTimeoutSeconds,
        CK_DbCommandTimeoutSeconds
    ];

    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLMetricsExportWorker> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly MLMetricsExportOptions _options;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    public MLMetricsExportWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLMetricsExportWorker> logger,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        MLMetricsExportOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _options = options ?? new MLMetricsExportOptions();
    }

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Exports rolling live ML model metrics into EngineConfig.",
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

                if (nowUtc - lastCycleStartUtc >= currentPollInterval)
                {
                    lastCycleStartUtc = nowUtc;
                    var started = Stopwatch.GetTimestamp();

                    try
                    {
                        _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                        var result = await RunOnceAsync(stoppingToken);
                        currentPollInterval = result.Settings.PollInterval;

                        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        _healthMonitor?.RecordBacklogDepth(WorkerName, result.CandidateModelCount);
                        _healthMonitor?.RecordCycleSuccess(WorkerName, elapsedMs);
                        _metrics?.WorkerCycleDurationMs.Record(elapsedMs, Tag("worker", WorkerName));

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: candidates={Candidates}, exported={Exported}, skipped={Skipped}, rowsLoaded={RowsLoaded}, configRows={ConfigRows}.",
                                WorkerName,
                                result.CandidateModelCount,
                                result.ModelsExported,
                                result.ModelsSkipped,
                                result.PredictionRowsLoaded,
                                result.ConfigRowsWritten);
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
                        _healthMonitor?.RecordRetry(WorkerName);
                        _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                        _metrics?.WorkerErrors.Add(1, Tag("worker", WorkerName), Tag("reason", "ml_metrics_export_cycle"));
                        _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                    }
                }

                if (lastSuccessUtc != DateTime.MinValue)
                {
                    _logger.LogTrace(
                        "{Worker}: last successful export was {AgeSeconds:F0}s ago.",
                        WorkerName,
                        (_timeProvider.GetUtcNow().UtcDateTime - lastSuccessUtc).TotalSeconds);
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
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<MLMetricsExportCycleResult> RunOnceAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

        var settings = await LoadSettingsAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, settings.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, settings.DbCommandTimeoutSeconds);

        if (!settings.Enabled)
            return RecordSkipped(settings, "disabled");

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate metric exports are possible in multi-instance deployments.",
                    WorkerName);
            }
        }
        else
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(settings.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
                return RecordSkipped(settings, "lock_busy");
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RunCoreAsync(readCtx, writeCtx, settings, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    private async Task<MLMetricsExportCycleResult> RunCoreAsync(
        DbContext readCtx,
        DbContext writeCtx,
        MLMetricsExportSettings settings,
        CancellationToken ct)
    {
        var candidates = await LoadCandidateModelsAsync(readCtx, settings.MaxModelsPerCycle, ct);
        _healthMonitor?.RecordBacklogDepth(WorkerName, candidates.TotalCandidateCount);

        if (candidates.Selected.Count == 0)
            return RecordSkipped(settings, "no_active_models");

        if (candidates.SkippedByLimit > 0)
        {
            _logger.LogWarning(
                "{Worker}: processing {Selected} of {Total} active metric candidates due to MaxModelsPerCycle={Limit}.",
                WorkerName,
                candidates.Selected.Count,
                candidates.TotalCandidateCount,
                settings.MaxModelsPerCycle);
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var windowStartUtc = nowUtc.AddDays(-settings.WindowDays);
        var configSpecs = new List<EngineConfigUpsertSpec>(candidates.Selected.Count * 20);
        var aliasModelIds = settings.WriteLegacySymbolTimeframeAliases
            ? SelectLegacyAliasModels(candidates.Selected)
            : new HashSet<long>();

        var exported = 0;
        var skipped = candidates.SkippedByLimit + candidates.SkippedInvalidModel;
        var predictionRowsLoaded = 0;
        var configRowsWritten = 0;

        foreach (var model in candidates.Selected)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var rows = await LoadPredictionRowsAsync(readCtx, model.Id, windowStartUtc, settings.MaxPredictionLogsPerModel, ct);
                predictionRowsLoaded += rows.Count;

                var snapshot = ComputeSnapshot(model, rows, windowStartUtc, nowUtc, settings);
                AddMetricSpecs(configSpecs, ModelPrefix(model.Id), snapshot);

                if (aliasModelIds.Contains(model.Id))
                    AddMetricSpecs(configSpecs, LegacyPrefix(model), snapshot, includeLegacyCompatibilityKeys: true);

                exported++;
                _logger.LogDebug(
                    "{Worker}: exported model={ModelId} {Symbol}/{Timeframe} status={Status} resolved={Resolved}/{Predictions}.",
                    WorkerName,
                    model.Id,
                    model.Symbol,
                    model.Timeframe,
                    snapshot.MetricsStatus,
                    snapshot.ResolvedPredictionCount,
                    snapshot.PredictionCount);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                skipped++;
                _metrics?.WorkerErrors.Add(1, Tag("worker", WorkerName), Tag("reason", "ml_metrics_export_model"));
                _logger.LogWarning(
                    ex,
                    "{Worker}: metric export failed for model {ModelId} ({Symbol}/{Timeframe}); skipping this model.",
                    WorkerName,
                    model.Id,
                    model.Symbol,
                    model.Timeframe);
            }

            if (configSpecs.Count >= 500)
            {
                configRowsWritten += configSpecs.Count;
                await EngineConfigUpsert.BatchUpsertAsync(writeCtx, configSpecs, ct);
                configSpecs.Clear();
                writeCtx.ChangeTracker.Clear();
            }
        }

        if (configSpecs.Count > 0)
        {
            configRowsWritten += configSpecs.Count;
            await EngineConfigUpsert.BatchUpsertAsync(writeCtx, configSpecs, ct);
            writeCtx.ChangeTracker.Clear();
        }

        return new MLMetricsExportCycleResult(
            settings,
            candidates.TotalCandidateCount,
            exported,
            skipped,
            predictionRowsLoaded,
            configRowsWritten,
            SkippedReason: null);
    }

    private async Task<IReadOnlyList<PredictionMetricRow>> LoadPredictionRowsAsync(
        DbContext readCtx,
        long modelId,
        DateTime windowStartUtc,
        int maxPredictionLogsPerModel,
        CancellationToken ct)
    {
        return await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(log => !log.IsDeleted
                       && log.MLModelId == modelId
                       && (log.PredictedAt >= windowStartUtc
                           || (log.OutcomeRecordedAt != null && log.OutcomeRecordedAt >= windowStartUtc)))
            .OrderByDescending(log => log.OutcomeRecordedAt ?? log.PredictedAt)
            .ThenByDescending(log => log.Id)
            .Select(log => new PredictionMetricRow
            {
                MLModelId = log.MLModelId,
                PredictedAt = log.PredictedAt,
                OutcomeRecordedAt = log.OutcomeRecordedAt,
                PredictedDirection = log.PredictedDirection,
                ActualDirection = log.ActualDirection,
                DirectionCorrect = log.DirectionCorrect,
                ConfidenceScore = log.ConfidenceScore,
                RawProbability = log.RawProbability,
                CalibratedProbability = log.CalibratedProbability,
                ServedCalibratedProbability = log.ServedCalibratedProbability,
                EnsembleDisagreement = log.EnsembleDisagreement,
                CommitteeDisagreement = log.CommitteeDisagreement,
                LatencyMs = log.LatencyMs
            })
            .Take(maxPredictionLogsPerModel)
            .ToListAsync(ct);
    }

    private static MLMetricsSnapshot ComputeSnapshot(
        ModelMetricCandidate model,
        IReadOnlyList<PredictionMetricRow> rows,
        DateTime windowStartUtc,
        DateTime nowUtc,
        MLMetricsExportSettings settings)
    {
        var predictionRows = rows
            .Where(row => row.PredictedAt >= windowStartUtc)
            .ToList();
        var resolvedRows = rows
            .Where(row => row.OutcomeRecordedAt >= windowStartUtc && row.DirectionCorrect.HasValue)
            .ToList();

        var correct = resolvedRows.Count(row => row.DirectionCorrect == true);
        double directionAccuracy = resolvedRows.Count > 0
            ? (double)correct / resolvedRows.Count
            : 0.0;

        double brierSum = 0.0;
        int brierCount = 0;
        foreach (var row in resolvedRows)
        {
            if (!row.ActualDirection.HasValue || !TryGetBuyProbability(row, out var pBuy))
                continue;

            var actualBuy = row.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
            brierSum += Math.Pow(pBuy - actualBuy, 2);
            brierCount++;
        }

        var disagreementValues = predictionRows
            .Select(GetDisagreement)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        var latencyValues = predictionRows
            .Where(row => row.LatencyMs is > 0)
            .Select(row => row.LatencyMs!.Value)
            .Order()
            .ToList();

        var modelAgeDays = model.ActivatedAt.HasValue
            ? Math.Max(0.0, (nowUtc - model.ActivatedAt.Value).TotalDays)
            : Math.Max(0.0, (nowUtc - model.TrainedAt).TotalDays);

        var status = DetermineStatus(
            predictionRows.Count,
            resolvedRows.Count,
            brierCount,
            rows.Count >= settings.MaxPredictionLogsPerModel,
            settings.MinResolvedSamples);

        return new MLMetricsSnapshot(
            ModelId: model.Id,
            Symbol: model.Symbol,
            Timeframe: model.Timeframe.ToString(),
            ModelVersion: model.ModelVersion,
            WindowStartUtc: windowStartUtc,
            LastUpdatedUtc: nowUtc,
            MetricsStatus: status,
            DirectionAccuracy: directionAccuracy,
            BrierScore: brierCount > 0 ? brierSum / brierCount : 0.0,
            EnsembleDisagreement: disagreementValues.Count > 0 ? disagreementValues.Average() : 0.0,
            InferenceLatencyAverageMs: latencyValues.Count > 0 ? latencyValues.Average() : 0.0,
            InferenceLatencyP95Ms: Percentile(latencyValues, 0.95),
            InferenceLatencyMaxMs: latencyValues.Count > 0 ? latencyValues[^1] : 0.0,
            PredictionCount: predictionRows.Count,
            ResolvedPredictionCount: resolvedRows.Count,
            CorrectPredictionCount: correct,
            BrierSampleCount: brierCount,
            DisagreementSampleCount: disagreementValues.Count,
            LatencySampleCount: latencyValues.Count,
            ModelAgeDays: modelAgeDays,
            LastPredictionAtUtc: predictionRows.Count > 0 ? predictionRows.Max(row => row.PredictedAt) : null,
            LastOutcomeAtUtc: resolvedRows.Count > 0 ? resolvedRows.Max(row => row.OutcomeRecordedAt) : null,
            IsTruncated: rows.Count >= settings.MaxPredictionLogsPerModel);
    }

    private static MLMetricsStatus DetermineStatus(
        int predictionCount,
        int resolvedCount,
        int brierSampleCount,
        bool isTruncated,
        int minResolvedSamples)
    {
        if (isTruncated)
            return MLMetricsStatus.Truncated;
        if (predictionCount == 0 && resolvedCount == 0)
            return MLMetricsStatus.NoRecentData;
        if (resolvedCount < minResolvedSamples)
            return MLMetricsStatus.InsufficientResolvedSamples;
        if (brierSampleCount == 0)
            return MLMetricsStatus.NoBrierSamples;

        return MLMetricsStatus.Healthy;
    }

    private static bool TryGetBuyProbability(PredictionMetricRow row, out double pBuy)
    {
        var exactProbability = row.ServedCalibratedProbability
                               ?? row.CalibratedProbability
                               ?? row.RawProbability;
        if (exactProbability.HasValue)
            return TryNormalizeProbability(exactProbability.Value, out pBuy);

        if (!TryNormalizeProbability(row.ConfidenceScore, out var confidence))
        {
            pBuy = 0.0;
            return false;
        }

        pBuy = row.PredictedDirection == TradeDirection.Buy
            ? confidence
            : 1.0 - confidence;
        return true;
    }

    private static bool TryNormalizeProbability(decimal value, out double normalized)
    {
        normalized = (double)value;
        if (!double.IsFinite(normalized))
            return false;

        normalized = Math.Clamp(normalized, 0.0, 1.0);
        return true;
    }

    private static double? GetDisagreement(PredictionMetricRow row)
    {
        var value = row.EnsembleDisagreement ?? row.CommitteeDisagreement;
        if (!value.HasValue)
            return null;

        var numeric = (double)value.Value;
        return double.IsFinite(numeric) ? Math.Max(0.0, numeric) : null;
    }

    private static double Percentile(IReadOnlyList<int> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
            return 0.0;

        var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Count - 1)];
    }

    private static void AddMetricSpecs(
        List<EngineConfigUpsertSpec> specs,
        string prefix,
        MLMetricsSnapshot snapshot,
        bool includeLegacyCompatibilityKeys = false)
    {
        AddString(specs, prefix, "ModelId", snapshot.ModelId.ToString(CultureInfo.InvariantCulture), "ML model id that produced this metrics block.");
        AddString(specs, prefix, "Symbol", snapshot.Symbol, "Model symbol for this metrics block.");
        AddString(specs, prefix, "Timeframe", snapshot.Timeframe, "Model timeframe for this metrics block.");
        AddString(specs, prefix, "ModelVersion", snapshot.ModelVersion, "Model version for this metrics block.");
        AddString(specs, prefix, "MetricsStatus", snapshot.MetricsStatus.ToString(), "Data-quality status for the exported metric values.");
        AddDecimal(specs, prefix, "DirectionAccuracy", snapshot.DirectionAccuracy, "Rolling resolved direction accuracy.");
        AddDecimal(specs, prefix, "BrierScore", snapshot.BrierScore, "Rolling Brier score over rows with actual direction and valid Buy probability.");
        AddDecimal(specs, prefix, "EnsembleDisagreement", snapshot.EnsembleDisagreement, "Mean ensemble or committee disagreement across recent predictions.");
        AddDecimal(specs, prefix, "InferenceLatencyMs", snapshot.InferenceLatencyAverageMs, "Average model inference latency in milliseconds.");
        AddDecimal(specs, prefix, "InferenceLatencyP95Ms", snapshot.InferenceLatencyP95Ms, "P95 model inference latency in milliseconds.");
        AddDecimal(specs, prefix, "InferenceLatencyMaxMs", snapshot.InferenceLatencyMaxMs, "Maximum model inference latency in milliseconds.");
        AddInt(specs, prefix, "PredictionCount", snapshot.PredictionCount, "Recent prediction rows in the metrics window.");
        AddInt(specs, prefix, "ResolvedPredictionCount", snapshot.ResolvedPredictionCount, "Recent resolved outcome rows in the metrics window.");
        AddInt(specs, prefix, "CorrectPredictionCount", snapshot.CorrectPredictionCount, "Recent resolved direction-correct rows in the metrics window.");
        AddInt(specs, prefix, "BrierSampleCount", snapshot.BrierSampleCount, "Resolved rows with valid probabilities used for Brier score.");
        AddInt(specs, prefix, "DisagreementSampleCount", snapshot.DisagreementSampleCount, "Prediction rows with disagreement telemetry.");
        AddInt(specs, prefix, "LatencySampleCount", snapshot.LatencySampleCount, "Prediction rows with latency telemetry.");
        AddDecimal(specs, prefix, "ModelAgeDays", snapshot.ModelAgeDays, "Model age in days from activation, falling back to training time.");
        AddDecimal(specs, prefix, "ModelAge", snapshot.ModelAgeDays, "Backward-compatible alias for model age in days.");
        AddString(specs, prefix, "WindowStart", snapshot.WindowStartUtc.ToString("O", CultureInfo.InvariantCulture), "UTC start of the metrics window.");
        AddString(specs, prefix, "LastPredictionAt", FormatTimestamp(snapshot.LastPredictionAtUtc), "Most recent prediction timestamp in the metrics window.");
        AddString(specs, prefix, "LastOutcomeAt", FormatTimestamp(snapshot.LastOutcomeAtUtc), "Most recent resolved outcome timestamp in the metrics window.");
        AddBool(specs, prefix, "IsTruncated", snapshot.IsTruncated, "True when the per-model prediction row cap was hit.");
        AddString(specs, prefix, "LastUpdated", snapshot.LastUpdatedUtc.ToString("O", CultureInfo.InvariantCulture), "UTC timestamp when this metrics block was exported.");

        if (includeLegacyCompatibilityKeys)
        {
            AddDecimal(specs, prefix, "Accuracy", snapshot.DirectionAccuracy, "Backward-compatible alias for DirectionAccuracy.");
            AddInt(specs, prefix, "SampleCount", snapshot.ResolvedPredictionCount, "Backward-compatible alias for ResolvedPredictionCount.");
        }
    }

    private static void AddString(
        List<EngineConfigUpsertSpec> specs,
        string prefix,
        string suffix,
        string value,
        string description)
        => specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:{suffix}",
            value,
            ConfigDataType.String,
            description,
            IsHotReloadable: false));

    private static void AddDecimal(
        List<EngineConfigUpsertSpec> specs,
        string prefix,
        string suffix,
        double value,
        string description)
        => specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:{suffix}",
            value.ToString("F6", CultureInfo.InvariantCulture),
            ConfigDataType.Decimal,
            description,
            IsHotReloadable: false));

    private static void AddInt(
        List<EngineConfigUpsertSpec> specs,
        string prefix,
        string suffix,
        int value,
        string description)
        => specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:{suffix}",
            value.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            description,
            IsHotReloadable: false));

    private static void AddBool(
        List<EngineConfigUpsertSpec> specs,
        string prefix,
        string suffix,
        bool value,
        string description)
        => specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:{suffix}",
            value ? "true" : "false",
            ConfigDataType.Bool,
            description,
            IsHotReloadable: false));

    private static string FormatTimestamp(DateTime? value)
        => value.HasValue
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
            : string.Empty;

    private static string ModelPrefix(long modelId)
        => $"{ModelMetricsPrefix}{modelId}";

    private static string LegacyPrefix(ModelMetricCandidate model)
        => $"MLMetrics:{NormalizeKeySegment(model.Symbol)}:{model.Timeframe}";

    private static string NormalizeKeySegment(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim();
        return trimmed.Replace(':', '_').ToUpperInvariant();
    }

    private static HashSet<long> SelectLegacyAliasModels(IReadOnlyList<ModelMetricCandidate> models)
        => models
            .GroupBy(model => new { Symbol = NormalizeKeySegment(model.Symbol), model.Timeframe })
            .Select(group => group
                .OrderByDescending(model => model.ActivatedAt ?? DateTime.MinValue)
                .ThenByDescending(model => model.TrainedAt)
                .ThenByDescending(model => model.Id)
                .First().Id)
            .ToHashSet();

    private async Task<ModelCandidateSelection> LoadCandidateModelsAsync(
        DbContext readCtx,
        int maxModelsPerCycle,
        CancellationToken ct)
    {
        var query = readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(model => model.IsActive
                         && !model.IsDeleted
                         && !model.IsMetaLearner
                         && !model.IsMamlInitializer
                         && (model.Status == MLModelStatus.Active || model.IsFallbackChampion));

        var total = await query.CountAsync(ct);
        var selected = await query
            .OrderBy(model => model.Symbol)
            .ThenBy(model => model.Timeframe)
            .ThenByDescending(model => model.ActivatedAt ?? DateTime.MinValue)
            .ThenByDescending(model => model.TrainedAt)
            .ThenByDescending(model => model.Id)
            .Take(maxModelsPerCycle)
            .Select(model => new ModelMetricCandidate(
                model.Id,
                model.Symbol,
                model.Timeframe,
                model.ModelVersion,
                model.TrainedAt,
                model.ActivatedAt))
            .ToListAsync(ct);

        var valid = selected
            .Where(model => !string.IsNullOrWhiteSpace(model.Symbol))
            .ToList();

        return new ModelCandidateSelection(
            valid,
            total,
            SkippedByLimit: Math.Max(0, total - selected.Count),
            SkippedInvalidModel: selected.Count - valid.Count);
    }

    private async Task<MLMetricsExportSettings> LoadSettingsAsync(
        DbContext readCtx,
        MLMetricsExportOptions options,
        CancellationToken ct)
    {
        var values = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => !config.IsDeleted && ConfigKeys.Contains(config.Key))
            .Select(config => new { config.Key, config.Value })
            .ToDictionaryAsync(config => config.Key, config => config.Value, ct);

        return new MLMetricsExportSettings(
            Enabled: GetBool(values, CK_Enabled, options.Enabled),
            InitialDelay: TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(GetInt(values, CK_InitialDelaySeconds, options.InitialDelaySeconds))),
            PollInterval: TimeSpan.FromSeconds(NormalizePollSeconds(GetInt(values, CK_PollSecs, options.PollIntervalSeconds))),
            WindowDays: NormalizeWindowDays(GetInt(values, CK_WindowDays, options.WindowDays)),
            MinResolvedSamples: NormalizeMinResolvedSamples(GetInt(values, CK_MinResolvedSamples, options.MinResolvedSamples)),
            MaxModelsPerCycle: NormalizeMaxModels(GetInt(values, CK_MaxModelsPerCycle, options.MaxModelsPerCycle)),
            MaxPredictionLogsPerModel: NormalizeMaxPredictionLogs(GetInt(values, CK_MaxPredictionLogsPerModel, options.MaxPredictionLogsPerModel)),
            WriteLegacySymbolTimeframeAliases: GetBool(values, CK_WriteLegacyAliases, options.WriteLegacySymbolTimeframeAliases),
            LockTimeoutSeconds: NormalizeLockTimeoutSeconds(GetInt(values, CK_LockTimeoutSeconds, options.LockTimeoutSeconds)),
            DbCommandTimeoutSeconds: NormalizeDbCommandTimeoutSeconds(GetInt(values, CK_DbCommandTimeoutSeconds, options.DbCommandTimeoutSeconds)));
    }

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
        => values.TryGetValue(key, out var rawValue)
           && int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
        => values.TryGetValue(key, out var rawValue)
           && bool.TryParse(rawValue, out var value)
            ? value
            : defaultValue;

    private static int NormalizeInitialDelaySeconds(int value)
        => Math.Clamp(value, 0, 86_400);

    private static int NormalizePollSeconds(int value)
        => Math.Clamp(value, 1, 86_400);

    private static int NormalizeWindowDays(int value)
        => Math.Clamp(value, 1, 3_650);

    private static int NormalizeMinResolvedSamples(int value)
        => Math.Clamp(value, 1, 1_000_000);

    private static int NormalizeMaxModels(int value)
        => Math.Clamp(value, 1, 250_000);

    private static int NormalizeMaxPredictionLogs(int value)
        => Math.Clamp(value, 10, 1_000_000);

    private static int NormalizeLockTimeoutSeconds(int value)
        => Math.Clamp(value, 0, 300);

    private static int NormalizeDbCommandTimeoutSeconds(int value)
        => Math.Clamp(value, 1, 600);

    private static void ApplyCommandTimeout(DbContext ctx, int timeoutSeconds)
    {
        if (ctx.Database.IsRelational())
            ctx.Database.SetCommandTimeout(timeoutSeconds);
    }

    private MLMetricsExportCycleResult RecordSkipped(MLMetricsExportSettings settings, string reason)
    {
        _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, reason);
        return MLMetricsExportCycleResult.Skipped(settings, reason);
    }

    private static TimeSpan CalculateBackoffDelay(int consecutiveFailures)
    {
        var exponentialSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(0, consecutiveFailures - 1));
        return TimeSpan.FromSeconds(Math.Min(exponentialSeconds, MaxRetryDelay.TotalSeconds));
    }

    private static KeyValuePair<string, object?> Tag(string key, object? value)
        => new(key, value);

    private sealed record ModelMetricCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        string ModelVersion,
        DateTime TrainedAt,
        DateTime? ActivatedAt);

    private sealed record ModelCandidateSelection(
        IReadOnlyList<ModelMetricCandidate> Selected,
        int TotalCandidateCount,
        int SkippedByLimit,
        int SkippedInvalidModel);

    private sealed class PredictionMetricRow
    {
        public long MLModelId { get; init; }
        public DateTime PredictedAt { get; init; }
        public DateTime? OutcomeRecordedAt { get; init; }
        public TradeDirection PredictedDirection { get; init; }
        public TradeDirection? ActualDirection { get; init; }
        public bool? DirectionCorrect { get; init; }
        public decimal ConfidenceScore { get; init; }
        public decimal? RawProbability { get; init; }
        public decimal? CalibratedProbability { get; init; }
        public decimal? ServedCalibratedProbability { get; init; }
        public decimal? EnsembleDisagreement { get; init; }
        public decimal? CommitteeDisagreement { get; init; }
        public int? LatencyMs { get; init; }
    }

    private sealed record MLMetricsSnapshot(
        long ModelId,
        string Symbol,
        string Timeframe,
        string ModelVersion,
        DateTime WindowStartUtc,
        DateTime LastUpdatedUtc,
        MLMetricsStatus MetricsStatus,
        double DirectionAccuracy,
        double BrierScore,
        double EnsembleDisagreement,
        double InferenceLatencyAverageMs,
        double InferenceLatencyP95Ms,
        double InferenceLatencyMaxMs,
        int PredictionCount,
        int ResolvedPredictionCount,
        int CorrectPredictionCount,
        int BrierSampleCount,
        int DisagreementSampleCount,
        int LatencySampleCount,
        double ModelAgeDays,
        DateTime? LastPredictionAtUtc,
        DateTime? LastOutcomeAtUtc,
        bool IsTruncated);

    private enum MLMetricsStatus
    {
        Healthy,
        InsufficientResolvedSamples,
        NoRecentData,
        NoBrierSamples,
        Truncated
    }
}

internal sealed record MLMetricsExportSettings(
    bool Enabled,
    TimeSpan InitialDelay,
    TimeSpan PollInterval,
    int WindowDays,
    int MinResolvedSamples,
    int MaxModelsPerCycle,
    int MaxPredictionLogsPerModel,
    bool WriteLegacySymbolTimeframeAliases,
    int LockTimeoutSeconds,
    int DbCommandTimeoutSeconds);

internal sealed record MLMetricsExportCycleResult(
    MLMetricsExportSettings Settings,
    int CandidateModelCount,
    int ModelsExported,
    int ModelsSkipped,
    int PredictionRowsLoaded,
    int ConfigRowsWritten,
    string? SkippedReason)
{
    public static MLMetricsExportCycleResult Skipped(MLMetricsExportSettings settings, string reason)
        => new(settings, 0, 0, 0, 0, 0, reason);
}
