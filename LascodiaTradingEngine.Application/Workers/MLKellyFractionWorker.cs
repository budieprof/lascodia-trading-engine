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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes conservative live Kelly caps for active ML models from recent resolved
/// served-champion outcomes.
/// </summary>
public sealed class MLKellyFractionWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLKellyFractionWorker);

    private const string DistributedLockKey = "workers:ml-kelly-fraction:cycle";
    private const string KellyConfigKeyPrefix = "MLKelly:";
    private const string KellyCapKeySuffix = ":KellyCap";
    private const string KellyCapCacheKeyPrefix = "MLKellyCap:";
    private const string ConfigPrefixUpper = "MLKELLYFRACTION:";

    private const string CK_Enabled = "MLKellyFraction:Enabled";
    private const string CK_InitialDelaySeconds = "MLKellyFraction:InitialDelaySeconds";
    private const string CK_PollSecs = "MLKellyFraction:PollIntervalSeconds";
    private const string CK_WindowDays = "MLKellyFraction:WindowDays";
    private const string CK_MinUsableSamples = "MLKellyFraction:MinUsableSamples";
    private const string CK_MinWins = "MLKellyFraction:MinWins";
    private const string CK_MinLosses = "MLKellyFraction:MinLosses";
    private const string CK_MaxAbsKelly = "MLKellyFraction:MaxAbsKelly";
    private const string CK_PriorTrades = "MLKellyFraction:PriorTrades";
    private const string CK_WinRateLowerBoundZ = "MLKellyFraction:WinRateLowerBoundZ";
    private const string CK_OutlierPercentile = "MLKellyFraction:OutlierPercentile";
    private const string CK_MaxOutcomeMagnitude = "MLKellyFraction:MaxOutcomeMagnitude";
    private const string CK_MaxModelsPerCycle = "MLKellyFraction:MaxModelsPerCycle";
    private const string CK_MaxPredictionLogsPerModel = "MLKellyFraction:MaxPredictionLogsPerModel";
    private const string CK_WriteNeutralCapOnInsufficientSamples = "MLKellyFraction:WriteNeutralCapOnInsufficientSamples";
    private const string CK_LockTimeoutSeconds = "MLKellyFraction:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSeconds = "MLKellyFraction:DbCommandTimeoutSeconds";

    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLKellyFractionWorker> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly IMemoryCache? _cache;
    private readonly MLKellyFractionOptions _options;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    public MLKellyFractionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLKellyFractionWorker> logger,
        TimeProvider? timeProvider = null,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        IMemoryCache? cache = null,
        MLKellyFractionOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _cache = cache;
        _options = options ?? new MLKellyFractionOptions();
    }

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    /// <summary>
    /// Hosted-service entry point. Executes after the shared startup sequencer and
    /// then re-runs on the configured cadence.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Computes conservative live Kelly caps for active ML models.",
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
                    _metrics?.MLKellyFractionTimeSinceLastSuccessSec.Record((nowUtc - lastSuccessUtc).TotalSeconds);

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
                        _metrics?.MLKellyFractionCycleDurationMs.Record(elapsedMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: candidates={Candidates}, evaluated={Evaluated}, reliable={Reliable}, suppressed={Suppressed}, lifted={Lifted}, skipped={Skipped}.",
                                WorkerName,
                                result.CandidateModelCount,
                                result.ModelsEvaluated,
                                result.ReliableLogsWritten,
                                result.ModelsSuppressed,
                                result.SuppressionsLifted,
                                result.ModelsSkipped);
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
                        _metrics?.WorkerErrors.Add(1, Tag("worker", WorkerName), Tag("reason", "ml_kelly_fraction_cycle"));
                        _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
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
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    /// <summary>
    /// Executes one Kelly computation cycle. Exposed internally for focused tests.
    /// </summary>
    internal async Task<KellyCycleResult> RunOnceAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

        var settings = await LoadSettingsAsync(readDb, _options, ct);
        ApplyCommandTimeout(readDb, settings.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeDb, settings.DbCommandTimeoutSeconds);

        if (!settings.Enabled)
        {
            RecordCycleSkipped("disabled");
            return KellyCycleResult.Skipped(settings, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics?.MLKellyFractionLockAttempts.Add(1, Tag("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate Kelly cycles are possible in multi-instance deployments.",
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
            {
                _metrics?.MLKellyFractionLockAttempts.Add(1, Tag("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                return KellyCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLKellyFractionLockAttempts.Add(1, Tag("outcome", "acquired"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RunCoreAsync(readDb, writeDb, settings, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    private async Task<KellyCycleResult> RunCoreAsync(
        DbContext readDb,
        DbContext writeDb,
        KellyWorkerSettings settings,
        CancellationToken ct)
    {
        var candidates = await LoadActiveModelCandidatesAsync(readDb, settings.MaxModelsPerCycle, ct);
        _healthMonitor?.RecordBacklogDepth(WorkerName, candidates.Selected.Count + candidates.SkippedByLimit);

        if (candidates.Selected.Count == 0)
        {
            RecordCycleSkipped("no_active_models");
            return new KellyCycleResult(settings, 0, 0, 0, 0, 0, 0, "no_active_models");
        }

        var evaluated = 0;
        var skipped = candidates.SkippedByLimit + candidates.SkippedInvalidModel;
        var reliable = 0;
        var suppressed = 0;
        var lifted = 0;

        if (candidates.SkippedByLimit > 0)
            _metrics?.MLKellyFractionModelsSkipped.Add(candidates.SkippedByLimit, Tag("reason", "cycle_limit"));
        if (candidates.SkippedInvalidModel > 0)
            _metrics?.MLKellyFractionModelsSkipped.Add(candidates.SkippedInvalidModel, Tag("reason", "invalid_model"));

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-settings.Sizing.WindowDays);

        foreach (var candidate in candidates.Selected)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var outcome = await ProcessModelAsync(readDb, writeDb, candidate, settings, cutoff, now, ct);
                if (outcome.Evaluated)
                    evaluated++;
                if (outcome.ReliableLogWritten)
                    reliable++;
                if (outcome.Suppressed)
                    suppressed++;
                if (outcome.SuppressionLifted)
                    lifted++;
                if (outcome.SkipReason is { Length: > 0 })
                {
                    skipped++;
                    _metrics?.MLKellyFractionModelsSkipped.Add(1, Tag("reason", outcome.SkipReason));
                }
                if (outcome.UsableSamples > 0)
                    _metrics?.MLKellyFractionUsableSamples.Record(outcome.UsableSamples, Tag("mode", outcome.NormalizationMode));
                if (outcome.KellyFraction.HasValue)
                    _metrics?.MLKellyFractionValue.Record(outcome.KellyFraction.Value, Tag("mode", outcome.NormalizationMode));
                if (outcome.HalfKelly.HasValue)
                    _metrics?.MLKellyFractionHalfKelly.Record(outcome.HalfKelly.Value, Tag("mode", outcome.NormalizationMode));

                writeDb.ChangeTracker.Clear();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                skipped++;
                _metrics?.MLKellyFractionModelsSkipped.Add(1, Tag("reason", "model_error"));
                _logger.LogWarning(
                    ex,
                    "{Worker}: Kelly computation failed for model {ModelId} ({Symbol}/{Timeframe}); skipping.",
                    WorkerName,
                    candidate.Id,
                    candidate.Symbol,
                    candidate.Timeframe);
                writeDb.ChangeTracker.Clear();
            }
        }

        if (evaluated > 0)
            _metrics?.MLKellyFractionModelsEvaluated.Add(evaluated);
        if (reliable > 0)
            _metrics?.MLKellyFractionLogsWritten.Add(reliable, Tag("reliable", true));
        if (suppressed > 0)
            _metrics?.MLKellyFractionNegativeEvSuppressions.Add(suppressed);
        if (lifted > 0)
            _metrics?.MLKellyFractionSuppressionsLifted.Add(lifted);

        return new KellyCycleResult(
            settings,
            candidates.Selected.Count + candidates.SkippedByLimit + candidates.SkippedInvalidModel,
            evaluated,
            skipped,
            reliable,
            suppressed,
            lifted,
            null);
    }

    private async Task<ModelKellyOutcome> ProcessModelAsync(
        DbContext readDb,
        DbContext writeDb,
        ActiveModelCandidate model,
        KellyWorkerSettings settings,
        DateTime cutoff,
        DateTime now,
        CancellationToken ct)
    {
        var tracked = await writeDb.Set<MLModel>().FindAsync([model.Id], ct);
        if (tracked is null || !IsRoutableModel(tracked))
            return ModelKellyOutcome.Skipped("not_routable");

        var configKey = BuildKellyCapConfigKey(model.Symbol, model.Timeframe, model.Id);
        var logs = await LoadResolvedLogsAsync(readDb, model.Id, cutoff, settings.MaxPredictionLogsPerModel, ct);

        if (logs.Count < settings.Sizing.MinUsableSamples)
        {
            await PersistNeutralKellyStateAsync(
                writeDb,
                tracked,
                configKey,
                logs.Count,
                0,
                0,
                0,
                0,
                "InsufficientResolvedSamples",
                settings,
                now,
                ct);
            return ModelKellyOutcome.Skipped("insufficient_resolved_samples", logs.Count);
        }

        var signalIds = logs.Select(l => l.TradeSignalId).Distinct().ToList();
        var signalSnapshots = await LoadSignalRiskSnapshotsAsync(readDb, signalIds, ct);
        var symbols = signalSnapshots.Values
            .Select(s => s.Symbol)
            .Concat(logs.Select(l => NormalizeSymbol(l.Symbol)))
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();
        var contractSizes = await LoadContractSizesAsync(readDb, symbols, ct);
        var signalToOrderIds = await LoadSignalOrderMapAsync(readDb, signalIds, ct);
        var orderIds = signalToOrderIds.Values.SelectMany(v => v).Distinct().ToList();
        var orderToPositionOutcomes = await LoadOrderPositionOutcomesAsync(readDb, orderIds, ct);

        var outcomes = BuildComparableOutcomes(
            logs,
            signalSnapshots,
            contractSizes,
            signalToOrderIds,
            orderToPositionOutcomes,
            out var pnlBasedCount);

        var population = SelectComparableOutcomePopulation(outcomes, settings.Sizing);
        if (population is null)
        {
            var insufficientStatus = outcomes.Count < settings.Sizing.MinUsableSamples
                ? "InsufficientUsableSamples"
                : "InsufficientComparableSamples";
            var insufficientReason = outcomes.Count < settings.Sizing.MinUsableSamples
                ? "insufficient_usable_samples"
                : "insufficient_comparable_samples";
            await PersistNeutralKellyStateAsync(
                writeDb,
                tracked,
                configKey,
                logs.Count,
                outcomes.Count,
                outcomes.Count(o => o.IsWin),
                outcomes.Count(o => !o.IsWin),
                pnlBasedCount,
                insufficientStatus,
                settings,
                now,
                ct);
            return ModelKellyOutcome.Skipped(insufficientReason, logs.Count, outcomes.Count);
        }

        var selectedOutcomes = population.Outcomes;
        var wins = selectedOutcomes.Where(o => o.IsWin).Select(o => o.Magnitude).ToArray();
        var losses = selectedOutcomes.Where(o => !o.IsWin).Select(o => o.Magnitude).ToArray();
        var clipped = ApplyOutcomeCap(wins, losses, settings.Sizing);

        var usableSamples = selectedOutcomes.Count;
        var p = (double)wins.Length / usableSamples;
        var q = 1 - p;
        var b = clipped.Wins.Average() / (clipped.Losses.Average() + 1e-8);
        var rawFStar = ClampFinite((p * b - q) / (b + 1e-8), -settings.Sizing.MaxAbsKelly, settings.Sizing.MaxAbsKelly);
        var conservativeP = ComputeConservativeWinRate(wins.Length, usableSamples, settings.Sizing);
        var conservativeQ = 1 - conservativeP;
        var shrinkage = ComputeShrinkage(usableSamples, settings.Sizing);
        var fStar = ClampFinite(
            shrinkage * ((conservativeP * b - conservativeQ) / (b + 1e-8)),
            -settings.Sizing.MaxAbsKelly,
            settings.Sizing.MaxAbsKelly);
        var halfKelly = ClampFinite(0.5 * fStar, -settings.Sizing.MaxAbsKelly, settings.Sizing.MaxAbsKelly);
        var negativeEv = fStar < 0.0;
        var deployedKellyCap = Math.Clamp(Math.Max(0.0, halfKelly), 0.0, settings.Sizing.MaxAbsKelly);
        var selectedPnlBasedCount = selectedOutcomes.Count(o => IsPnlBasedMode(o.NormalizationMode));

        writeDb.Set<MLKellyFractionLog>().Add(new MLKellyFractionLog
        {
            MLModelId = tracked.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe.ToString(),
            KellyFraction = fStar,
            RawKellyFraction = rawFStar,
            HalfKelly = halfKelly,
            WinRate = p,
            WinLossRatio = ClampFinite(b, 0.0, 1_000_000.0),
            ConservativeWinRate = conservativeP,
            ShrinkageFactor = shrinkage,
            OutlierCap = clipped.Cap,
            NormalizationMode = population.NormalizationMode,
            NegativeEV = negativeEv,
            TotalResolvedSamples = logs.Count,
            UsableSamples = usableSamples,
            WinCount = wins.Length,
            LossCount = losses.Length,
            PnlBasedSamples = selectedPnlBasedCount,
            IsReliable = true,
            Status = "Computed",
            ComputedAt = now
        });

        if (negativeEv)
            tracked.IsSuppressed = true;

        await UpsertConfigAsync(
            writeDb,
            configKey,
            deployedKellyCap.ToString("F4", CultureInfo.InvariantCulture),
            ct);
        EvictKellyCapCache(model.Symbol, model.Timeframe, model.Id);

        await writeDb.SaveChangesAsync(ct);

        var suppressionLifted = false;
        if (!negativeEv && tracked.IsSuppressed && await MLSuppressionStateHelper.CanLiftSuppressionAsync(writeDb, tracked, ct))
        {
            tracked.IsSuppressed = false;
            await writeDb.SaveChangesAsync(ct);
            suppressionLifted = true;
        }

        _logger.LogInformation(
            "{Worker}: {Symbol}/{Timeframe} model {ModelId} mode={Mode} rawF*={Raw:F4} f*={F:F4} halfKelly={Half:F4} winRate={P:F3} pLcb={PLcb:F3} b={B:F3} shrink={Shrink:F3} negEV={NegativeEV}.",
            WorkerName,
            model.Symbol,
            model.Timeframe,
            model.Id,
            population.NormalizationMode,
            rawFStar,
            fStar,
            halfKelly,
            p,
            conservativeP,
            b,
            shrinkage,
            negativeEv);

        return ModelKellyOutcome.Computed(
            reliable: true,
            suppressed: negativeEv,
            suppressionLifted: suppressionLifted,
            usableSamples: usableSamples,
            normalizationMode: population.NormalizationMode,
            kellyFraction: fStar,
            halfKelly: halfKelly);
    }

    private static async Task<CandidateSelection> LoadActiveModelCandidatesAsync(
        DbContext readDb,
        int maxModelsPerCycle,
        CancellationToken ct)
    {
        var query = readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer
                        && (m.Status == MLModelStatus.Active || m.IsFallbackChampion)
                        && m.ModelBytes != null
                        && m.ModelBytes.Length > 0);

        var rows = await query
            .OrderByDescending(m => m.ActivatedAt ?? m.TrainedAt)
            .ThenBy(m => m.Id)
            .Take(maxModelsPerCycle + 1)
            .Select(m => new ActiveModelCandidate(
                m.Id,
                NormalizeSymbol(m.Symbol),
                m.Timeframe,
                m.ActivatedAt ?? m.TrainedAt))
            .ToListAsync(ct);

        var invalid = rows.Count(r => string.IsNullOrWhiteSpace(r.Symbol));
        rows = rows.Where(r => !string.IsNullOrWhiteSpace(r.Symbol)).ToList();

        var skippedByLimit = 0;
        if (rows.Count > maxModelsPerCycle)
        {
            rows.RemoveAt(rows.Count - 1);
            var totalActive = await query.CountAsync(ct);
            skippedByLimit = Math.Max(0, totalActive - maxModelsPerCycle - invalid);
        }

        return new CandidateSelection(rows, skippedByLimit, invalid);
    }

    private static async Task<List<KellyPredictionLog>> LoadResolvedLogsAsync(
        DbContext readDb,
        long modelId,
        DateTime cutoff,
        int maxPredictionLogsPerModel,
        CancellationToken ct)
    {
        var logs = await readDb.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId == modelId
                        && !l.IsDeleted
                        && l.ModelRole == ModelRole.Champion
                        && l.TradeSignalId > 0
                        && l.TradeSignal.MLModelId == modelId
                        && l.DirectionCorrect != null
                        && l.OutcomeRecordedAt != null
                        && l.OutcomeRecordedAt >= cutoff)
            .OrderByDescending(l => l.OutcomeRecordedAt)
            .ThenByDescending(l => l.Id)
            .Take(maxPredictionLogsPerModel)
            .Select(l => new KellyPredictionLog(
                l.Id,
                l.TradeSignalId,
                l.Symbol,
                l.WasProfitable,
                l.DirectionCorrect,
                l.ActualMagnitudePips,
                l.OutcomeRecordedAt!.Value))
            .ToListAsync(ct);

        logs.Sort(static (a, b) =>
        {
            var byTime = a.OutcomeRecordedAt.CompareTo(b.OutcomeRecordedAt);
            return byTime != 0 ? byTime : a.Id.CompareTo(b.Id);
        });
        return logs;
    }

    private static async Task<Dictionary<long, SignalRiskSnapshot>> LoadSignalRiskSnapshotsAsync(
        DbContext readDb,
        IReadOnlyCollection<long> signalIds,
        CancellationToken ct)
    {
        if (signalIds.Count == 0)
            return new Dictionary<long, SignalRiskSnapshot>();

        var rows = await readDb.Set<TradeSignal>()
            .AsNoTracking()
            .Where(s => signalIds.Contains(s.Id) && !s.IsDeleted)
            .Select(s => new SignalRiskSnapshot(s.Id, NormalizeSymbol(s.Symbol), s.EntryPrice, s.StopLoss))
            .ToListAsync(ct);

        return rows.ToDictionary(s => s.Id);
    }

    private static async Task<Dictionary<string, decimal>> LoadContractSizesAsync(
        DbContext readDb,
        IReadOnlyCollection<string> symbols,
        CancellationToken ct)
    {
        if (symbols.Count == 0)
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        var rows = await readDb.Set<CurrencyPair>()
            .AsNoTracking()
            .Where(c => !c.IsDeleted && symbols.Contains(c.Symbol))
            .Select(c => new { c.Symbol, c.ContractSize })
            .ToListAsync(ct);

        return rows
            .GroupBy(c => NormalizeSymbol(c.Symbol), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToDictionary(g => g.Key, g => g.First().ContractSize, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<long, HashSet<long>>> LoadSignalOrderMapAsync(
        DbContext readDb,
        IReadOnlyCollection<long> signalIds,
        CancellationToken ct)
    {
        if (signalIds.Count == 0)
            return new Dictionary<long, HashSet<long>>();

        var rows = await readDb.Set<Order>()
            .AsNoTracking()
            .Where(o => o.TradeSignalId != null && signalIds.Contains(o.TradeSignalId.Value) && !o.IsDeleted)
            .Select(o => new { TradeSignalId = o.TradeSignalId!.Value, o.Id })
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.TradeSignalId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToHashSet());
    }

    private static async Task<Dictionary<long, List<OrderPositionOutcome>>> LoadOrderPositionOutcomesAsync(
        DbContext readDb,
        IReadOnlyCollection<long> orderIds,
        CancellationToken ct)
    {
        if (orderIds.Count == 0)
            return new Dictionary<long, List<OrderPositionOutcome>>();

        var rows = await readDb.Set<Position>()
            .AsNoTracking()
            .Where(p => p.Status == PositionStatus.Closed
                        && p.OpenOrderId != null
                        && orderIds.Contains(p.OpenOrderId.Value)
                        && p.OpenLots > 0m
                        && !p.IsDeleted)
            .Select(p => new OrderPositionOutcome(
                p.OpenOrderId!.Value,
                NormalizeSymbol(p.Symbol),
                p.RealizedPnL + p.Swap - p.Commission,
                p.OpenLots))
            .ToListAsync(ct);

        return rows.GroupBy(x => x.OpenOrderId).ToDictionary(g => g.Key, g => g.ToList());
    }

    private static List<KellyOutcome> BuildComparableOutcomes(
        IReadOnlyCollection<KellyPredictionLog> logs,
        IReadOnlyDictionary<long, SignalRiskSnapshot> signalSnapshots,
        IReadOnlyDictionary<string, decimal> contractSizes,
        IReadOnlyDictionary<long, HashSet<long>> signalToOrderIds,
        IReadOnlyDictionary<long, List<OrderPositionOutcome>> orderToPositionOutcomes,
        out int pnlBasedCount)
    {
        var outcomes = new List<KellyOutcome>(logs.Count);
        pnlBasedCount = 0;

        foreach (var log in logs)
        {
            if (signalToOrderIds.TryGetValue(log.TradeSignalId, out var orderIds))
            {
                var totalPnl = 0m;
                var totalRiskAmount = 0m;
                var totalLots = 0m;
                var hasPnl = false;

                foreach (var orderId in orderIds)
                {
                    if (!orderToPositionOutcomes.TryGetValue(orderId, out var positionOutcomes))
                        continue;

                    foreach (var positionOutcome in positionOutcomes)
                    {
                        totalPnl += positionOutcome.NetPnl;
                        totalLots += positionOutcome.OpenLots;

                        if (signalSnapshots.TryGetValue(log.TradeSignalId, out var signalSnapshot) &&
                            contractSizes.TryGetValue(positionOutcome.Symbol, out var contractSize) &&
                            TryResolveRiskAmount(signalSnapshot, contractSize, positionOutcome.OpenLots, out var riskAmount))
                        {
                            totalRiskAmount += riskAmount;
                        }

                        hasPnl = true;
                    }
                }

                var pnlPerLot = totalLots > 0m ? totalPnl / totalLots : 0m;
                if (hasPnl && TryClassifyEconomicOutcome(totalPnl, pnlPerLot, totalRiskAmount, out var pnlOutcome))
                {
                    outcomes.Add(pnlOutcome);
                    pnlBasedCount++;
                    continue;
                }
            }

            if (TryClassifyFallbackOutcome(log, out var fallbackOutcome))
                outcomes.Add(fallbackOutcome);
        }

        return outcomes;
    }

    private static OutcomePopulation? SelectComparableOutcomePopulation(
        IReadOnlyList<KellyOutcome> outcomes,
        KellyRuntimeConfig config)
    {
        foreach (var mode in new[]
                 {
                     NormalizationModes.RiskMultiple,
                     NormalizationModes.PnlPerLot,
                     NormalizationModes.FallbackMagnitude
                 })
        {
            var selected = outcomes.Where(o => o.NormalizationMode == mode).ToList();
            if (HasEnoughSamples(selected, config))
                return new OutcomePopulation(mode, selected);
        }

        return null;
    }

    private static bool HasEnoughSamples(IReadOnlyCollection<KellyOutcome> outcomes, KellyRuntimeConfig config)
    {
        if (outcomes.Count < config.MinUsableSamples)
            return false;

        var wins = outcomes.Count(o => o.IsWin);
        var losses = outcomes.Count - wins;
        return wins >= config.MinWins && losses >= config.MinLosses;
    }

    private async Task<KellyWorkerSettings> LoadSettingsAsync(
        DbContext readDb,
        MLKellyFractionOptions options,
        CancellationToken ct)
    {
        var fallback = KellyWorkerSettings.FromOptions(options);

        Dictionary<string, string?> values;
        try
        {
            var rows = await readDb.Set<EngineConfig>()
                .AsNoTracking()
                .Where(c => c.Key.ToUpper().StartsWith(ConfigPrefixUpper) && !c.IsDeleted)
                .Select(c => new { c.Id, c.Key, Value = (string?)c.Value, c.LastUpdatedAt })
                .ToListAsync(ct);

            values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.OrderBy(r => r.LastUpdatedAt).ThenBy(r => r.Id))
            {
                if (!string.IsNullOrWhiteSpace(row.Key))
                    values[row.Key.Trim()] = row.Value;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "{Worker}: failed to read EngineConfig Kelly settings; using options/defaults.",
                WorkerName);
            values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        var maxLogs = GetInt(values, CK_MaxPredictionLogsPerModel, fallback.MaxPredictionLogsPerModel, 10, 1_000_000);
        var minUsable = GetInt(values, CK_MinUsableSamples, fallback.Sizing.MinUsableSamples, 2, 1_000_000);
        maxLogs = Math.Max(maxLogs, minUsable);

        var minWins = GetInt(values, CK_MinWins, fallback.Sizing.MinWins, 1, 1_000_000);
        var minLosses = GetInt(values, CK_MinLosses, fallback.Sizing.MinLosses, 1, 1_000_000);
        minWins = Math.Min(minWins, minUsable);
        minLosses = Math.Min(minLosses, minUsable);

        var sizing = new KellyRuntimeConfig(
            WindowDays: GetInt(values, CK_WindowDays, fallback.Sizing.WindowDays, 1, 3650),
            MinUsableSamples: minUsable,
            MinWins: minWins,
            MinLosses: minLosses,
            MaxAbsKelly: GetDouble(values, CK_MaxAbsKelly, fallback.Sizing.MaxAbsKelly, 0.001, 1.0),
            PriorTrades: GetDouble(values, CK_PriorTrades, fallback.Sizing.PriorTrades, 0.0, 1_000.0),
            WinRateLowerBoundZ: GetDouble(values, CK_WinRateLowerBoundZ, fallback.Sizing.WinRateLowerBoundZ, 0.0, 3.0),
            OutlierPercentile: GetDouble(values, CK_OutlierPercentile, fallback.Sizing.OutlierPercentile, 0.50, 1.0),
            MaxOutcomeMagnitude: GetDouble(values, CK_MaxOutcomeMagnitude, fallback.Sizing.MaxOutcomeMagnitude, 0.001, 1_000_000.0));

        return new KellyWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, fallback.Enabled),
            InitialDelay: TimeSpan.FromSeconds(GetInt(values, CK_InitialDelaySeconds, (int)fallback.InitialDelay.TotalSeconds, 0, 86_400)),
            PollInterval: TimeSpan.FromSeconds(GetInt(values, CK_PollSecs, (int)fallback.PollInterval.TotalSeconds, 1, 86_400)),
            MaxModelsPerCycle: GetInt(values, CK_MaxModelsPerCycle, fallback.MaxModelsPerCycle, 1, 250_000),
            MaxPredictionLogsPerModel: maxLogs,
            WriteNeutralCapOnInsufficientSamples: GetBool(values, CK_WriteNeutralCapOnInsufficientSamples, fallback.WriteNeutralCapOnInsufficientSamples),
            LockTimeoutSeconds: GetInt(values, CK_LockTimeoutSeconds, fallback.LockTimeoutSeconds, 0, 300),
            DbCommandTimeoutSeconds: GetInt(values, CK_DbCommandTimeoutSeconds, fallback.DbCommandTimeoutSeconds, 1, 600),
            Sizing: sizing);
    }

    private static Task UpsertConfigAsync(
        DbContext writeCtx,
        string key,
        string value,
        CancellationToken ct)
        => EngineConfigUpsert.UpsertAsync(writeCtx, key, value, dataType: ConfigDataType.Decimal, ct: ct);

    private static bool TryResolveRiskAmount(
        SignalRiskSnapshot signal,
        decimal contractSize,
        decimal lots,
        out decimal riskAmount)
    {
        riskAmount = 0m;
        if (!signal.StopLoss.HasValue || signal.EntryPrice <= 0m || signal.StopLoss.Value <= 0m ||
            contractSize <= 0m || lots <= 0m)
            return false;

        riskAmount = Math.Abs(signal.EntryPrice - signal.StopLoss.Value) * contractSize * lots;
        return riskAmount > 0m;
    }

    internal static bool TryClassifyEconomicOutcome(
        decimal pnl,
        decimal pnlPerLot,
        decimal riskAmount,
        out KellyOutcome outcome)
    {
        outcome = default;
        var hasRisk = riskAmount > 0m;
        var magnitude = hasRisk
            ? (double)Math.Abs(pnl / riskAmount)
            : (double)Math.Abs(pnlPerLot);
        if (magnitude <= 0.0 || !double.IsFinite(magnitude))
            return false;

        outcome = new KellyOutcome(
            pnl > 0m,
            magnitude,
            hasRisk ? NormalizationModes.RiskMultiple : NormalizationModes.PnlPerLot);
        return true;
    }

    internal static bool TryClassifyEconomicOutcome(decimal pnl, out KellyOutcome outcome)
        => TryClassifyEconomicOutcome(pnl, pnl, 0m, out outcome);

    internal static bool TryClassifyFallbackOutcome(MLModelPredictionLog log, out KellyOutcome outcome)
    {
        outcome = default;
        if (log.ActualMagnitudePips is null)
            return false;

        return TryClassifyFallbackOutcome(
            log.WasProfitable,
            log.DirectionCorrect,
            log.ActualMagnitudePips.Value,
            out outcome);
    }

    private static bool TryClassifyFallbackOutcome(KellyPredictionLog log, out KellyOutcome outcome)
    {
        outcome = default;
        if (log.ActualMagnitudePips is null)
            return false;

        return TryClassifyFallbackOutcome(
            log.WasProfitable,
            log.DirectionCorrect,
            log.ActualMagnitudePips.Value,
            out outcome);
    }

    private static bool TryClassifyFallbackOutcome(
        bool? wasProfitable,
        bool? directionCorrect,
        decimal actualMagnitudePips,
        out KellyOutcome outcome)
    {
        outcome = default;
        var magnitude = (double)Math.Abs(actualMagnitudePips);
        if (magnitude <= 0.0 || !double.IsFinite(magnitude))
            return false;

        var isWin = wasProfitable ?? (directionCorrect == true);
        outcome = new KellyOutcome(isWin, magnitude, NormalizationModes.FallbackMagnitude);
        return true;
    }

    internal static CappedOutcomes ApplyOutcomeCap(
        IReadOnlyList<double> wins,
        IReadOnlyList<double> losses,
        KellyRuntimeConfig config)
    {
        var all = wins.Concat(losses)
            .Where(v => double.IsFinite(v) && v > 0.0)
            .OrderBy(v => v)
            .ToArray();
        if (all.Length == 0)
            return new CappedOutcomes(wins.ToArray(), losses.ToArray(), config.MaxOutcomeMagnitude);

        var index = Math.Clamp(
            (int)Math.Ceiling(config.OutlierPercentile * all.Length) - 1,
            0,
            all.Length - 1);
        var cap = Math.Min(all[index], config.MaxOutcomeMagnitude);

        return new CappedOutcomes(
            wins.Select(w => Math.Min(w, cap)).ToArray(),
            losses.Select(l => Math.Min(l, cap)).ToArray(),
            cap);
    }

    internal static double ComputeConservativeWinRate(int wins, int total, KellyRuntimeConfig config)
    {
        if (total <= 0)
            return 0.0;

        var prior = config.PriorTrades;
        var posteriorN = total + prior;
        var posteriorMean = (wins + 0.5 * prior) / posteriorN;
        var posteriorVariance = posteriorMean * (1.0 - posteriorMean) / Math.Max(posteriorN + 1.0, 1.0);

        return Math.Clamp(
            posteriorMean - config.WinRateLowerBoundZ * Math.Sqrt(Math.Max(0.0, posteriorVariance)),
            0.0,
            1.0);
    }

    internal static double ComputeShrinkage(int total, KellyRuntimeConfig config)
    {
        if (total <= 0)
            return 0.0;
        return total / (total + config.PriorTrades);
    }

    private static async Task PersistNeutralKellyStateAsync(
        DbContext writeDb,
        MLModel model,
        string configKey,
        int totalResolvedSamples,
        int usableSamples,
        int winCount,
        int lossCount,
        int pnlBasedSamples,
        string status,
        KellyWorkerSettings settings,
        DateTime computedAt,
        CancellationToken ct)
    {
        writeDb.Set<MLKellyFractionLog>().Add(new MLKellyFractionLog
        {
            MLModelId = model.Id,
            Symbol = NormalizeSymbol(model.Symbol),
            Timeframe = model.Timeframe.ToString(),
            KellyFraction = 0.0,
            RawKellyFraction = 0.0,
            HalfKelly = 0.0,
            WinRate = 0.5,
            WinLossRatio = 1.0,
            ConservativeWinRate = 0.5,
            ShrinkageFactor = 0.0,
            OutlierCap = 0.0,
            NormalizationMode = NormalizationModes.Unknown,
            NegativeEV = false,
            TotalResolvedSamples = totalResolvedSamples,
            UsableSamples = usableSamples,
            WinCount = winCount,
            LossCount = lossCount,
            PnlBasedSamples = pnlBasedSamples,
            IsReliable = false,
            Status = status,
            ComputedAt = computedAt
        });

        if (settings.WriteNeutralCapOnInsufficientSamples)
            await UpsertConfigAsync(writeDb, configKey, "0.0000", ct);

        await writeDb.SaveChangesAsync(ct);
    }

    private void EvictKellyCapCache(string symbol, Timeframe timeframe, long modelId)
    {
        if (_cache is null)
            return;

        _cache.Remove($"{KellyCapCacheKeyPrefix}{symbol}:{timeframe}:{modelId}:KellyCap");
        _cache.Remove($"{KellyCapCacheKeyPrefix}{symbol}:{timeframe}:KellyCap");
    }

    private static bool IsRoutableModel(MLModel model)
        => model.IsActive
           && !model.IsDeleted
           && !model.IsMetaLearner
           && !model.IsMamlInitializer
           && (model.Status == MLModelStatus.Active || model.IsFallbackChampion)
           && model.ModelBytes is { Length: > 0 };

    private static bool IsPnlBasedMode(string mode)
        => mode is NormalizationModes.RiskMultiple or NormalizationModes.PnlPerLot;

    private static string BuildKellyCapConfigKey(string symbol, Timeframe timeframe, long modelId)
        => $"{KellyConfigKeyPrefix}{NormalizeSymbol(symbol)}:{timeframe}:{modelId}{KellyCapKeySuffix}";

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLKellyFractionCyclesSkipped.Add(1, Tag("reason", reason));

    private static void ApplyCommandTimeout(DbContext db, int seconds)
    {
        if (db.Database.IsRelational())
            db.Database.SetCommandTimeout(seconds);
    }

    private TimeSpan CalculateBackoffDelay(int consecutiveFailures)
    {
        var exponent = Math.Min(6, Math.Max(0, consecutiveFailures - 1));
        var delay = TimeSpan.FromTicks(InitialRetryDelay.Ticks * (1L << exponent));
        return delay <= MaxRetryDelay ? delay : MaxRetryDelay;
    }

    private static int GetInt(
        IReadOnlyDictionary<string, string?> values,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        return values.TryGetValue(key, out var raw) &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : Math.Clamp(defaultValue, min, max);
    }

    private static double GetDouble(
        IReadOnlyDictionary<string, string?> values,
        string key,
        double defaultValue,
        double min,
        double max)
    {
        return values.TryGetValue(key, out var raw) &&
               double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
               double.IsFinite(parsed)
            ? Math.Clamp(parsed, min, max)
            : Math.Clamp(defaultValue, min, max);
    }

    private static bool GetBool(
        IReadOnlyDictionary<string, string?> values,
        string key,
        bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (bool.TryParse(raw, out var parsed))
            return parsed;

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            ? true
            : raw.Equals("0", StringComparison.OrdinalIgnoreCase) ||
              raw.Equals("no", StringComparison.OrdinalIgnoreCase)
                ? false
                : defaultValue;
    }

    private static double ClampFinite(double value, double min, double max)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : 0.0;

    private static int NormalizeInitialDelaySeconds(int value)
        => value is >= 0 and <= 86_400 ? value : 0;

    private static int NormalizePollSeconds(int value)
        => value is >= 1 and <= 86_400 ? value : 86_400;

    private static string NormalizeSymbol(string? symbol)
        => string.IsNullOrWhiteSpace(symbol) ? string.Empty : symbol.Trim().ToUpperInvariant();

    private static KeyValuePair<string, object?> Tag(string name, object? value)
        => new(name, value);

    internal static class NormalizationModes
    {
        internal const string Unknown = "Unknown";
        internal const string RiskMultiple = "RiskMultiple";
        internal const string PnlPerLot = "PnlPerLot";
        internal const string FallbackMagnitude = "FallbackMagnitude";
    }

    internal readonly record struct KellyOutcome(bool IsWin, double Magnitude, string NormalizationMode);

    internal sealed record CappedOutcomes(double[] Wins, double[] Losses, double Cap);

    private sealed record SignalRiskSnapshot(long Id, string Symbol, decimal EntryPrice, decimal? StopLoss);

    private sealed record OrderPositionOutcome(long OpenOrderId, string Symbol, decimal NetPnl, decimal OpenLots);

    private sealed record ActiveModelCandidate(long Id, string Symbol, Timeframe Timeframe, DateTime ActivatedAt);

    private sealed record CandidateSelection(
        List<ActiveModelCandidate> Selected,
        int SkippedByLimit,
        int SkippedInvalidModel);

    private sealed record KellyPredictionLog(
        long Id,
        long TradeSignalId,
        string Symbol,
        bool? WasProfitable,
        bool? DirectionCorrect,
        decimal? ActualMagnitudePips,
        DateTime OutcomeRecordedAt);

    private sealed record OutcomePopulation(string NormalizationMode, List<KellyOutcome> Outcomes);

    private sealed record ModelKellyOutcome(
        bool Evaluated,
        bool ReliableLogWritten,
        bool Suppressed,
        bool SuppressionLifted,
        int TotalResolvedSamples,
        int UsableSamples,
        string NormalizationMode,
        double? KellyFraction,
        double? HalfKelly,
        string? SkipReason)
    {
        public static ModelKellyOutcome Skipped(string reason, int totalResolvedSamples = 0, int usableSamples = 0)
            => new(false, false, false, false, totalResolvedSamples, usableSamples, NormalizationModes.Unknown, null, null, reason);

        public static ModelKellyOutcome Computed(
            bool reliable,
            bool suppressed,
            bool suppressionLifted,
            int usableSamples,
            string normalizationMode,
            double kellyFraction,
            double halfKelly)
            => new(true, reliable, suppressed, suppressionLifted, usableSamples, usableSamples, normalizationMode, kellyFraction, halfKelly, null);
    }

    internal sealed record KellyCycleResult(
        KellyWorkerSettings Settings,
        int CandidateModelCount,
        int ModelsEvaluated,
        int ModelsSkipped,
        int ReliableLogsWritten,
        int ModelsSuppressed,
        int SuppressionsLifted,
        string? SkippedReason)
    {
        public static KellyCycleResult Skipped(KellyWorkerSettings settings, string reason)
            => new(settings, 0, 0, 0, 0, 0, 0, reason);
    }

    internal sealed record KellyRuntimeConfig(
        int WindowDays,
        int MinUsableSamples,
        int MinWins,
        int MinLosses,
        double MaxAbsKelly,
        double PriorTrades,
        double WinRateLowerBoundZ,
        double OutlierPercentile,
        double MaxOutcomeMagnitude);

    internal sealed record KellyWorkerSettings(
        bool Enabled,
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int MaxModelsPerCycle,
        int MaxPredictionLogsPerModel,
        bool WriteNeutralCapOnInsufficientSamples,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds,
        KellyRuntimeConfig Sizing)
    {
        public static KellyWorkerSettings FromOptions(MLKellyFractionOptions options)
        {
            var minUsable = Math.Clamp(options.MinUsableSamples, 2, 1_000_000);
            var minWins = Math.Min(Math.Clamp(options.MinWins, 1, 1_000_000), minUsable);
            var minLosses = Math.Min(Math.Clamp(options.MinLosses, 1, 1_000_000), minUsable);
            var maxLogs = Math.Max(Math.Clamp(options.MaxPredictionLogsPerModel, 10, 1_000_000), minUsable);

            return new KellyWorkerSettings(
                options.Enabled,
                TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(options.InitialDelaySeconds)),
                TimeSpan.FromSeconds(NormalizePollSeconds(options.PollIntervalSeconds)),
                Math.Clamp(options.MaxModelsPerCycle, 1, 250_000),
                maxLogs,
                options.WriteNeutralCapOnInsufficientSamples,
                Math.Clamp(options.LockTimeoutSeconds, 0, 300),
                Math.Clamp(options.DbCommandTimeoutSeconds, 1, 600),
                new KellyRuntimeConfig(
                    Math.Clamp(options.WindowDays, 1, 3650),
                    minUsable,
                    minWins,
                    minLosses,
                    double.IsFinite(options.MaxAbsKelly) ? Math.Clamp(options.MaxAbsKelly, 0.001, 1.0) : 0.25,
                    double.IsFinite(options.PriorTrades) ? Math.Clamp(options.PriorTrades, 0.0, 1_000.0) : 20.0,
                    double.IsFinite(options.WinRateLowerBoundZ) ? Math.Clamp(options.WinRateLowerBoundZ, 0.0, 3.0) : 1.0,
                    double.IsFinite(options.OutlierPercentile) ? Math.Clamp(options.OutlierPercentile, 0.50, 1.0) : 0.95,
                    double.IsFinite(options.MaxOutcomeMagnitude)
                        ? Math.Clamp(options.MaxOutcomeMagnitude, 0.001, 1_000_000.0)
                        : 10.0));
        }
    }
}
