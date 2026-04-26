using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically generates mutated offspring of strong live strategies, persists them as paused
/// draft candidates, and queues their initial validation backtests so they actually enter the
/// existing screening pipeline.
///
/// <para>
/// Idempotency is enforced by the worker itself using a stable candidate identity built from
/// <see cref="StrategyType"/>, symbol, timeframe, and canonicalized parameter JSON. This is
/// intentionally stricter than relying on the database because Draft candidates are allowed to
/// coexist under the active-strategy uniqueness filter.
/// </para>
/// </summary>
public sealed partial class EvolutionaryGeneratorWorker : BackgroundService
{
    internal const string WorkerName = nameof(EvolutionaryGeneratorWorker);

    private const string CK_Enabled = "Evolution:Enabled";
    private const string CK_PollSeconds = "Evolution:PollIntervalSeconds";
    private const string CK_MaxOffspring = "Evolution:MaxOffspringPerCycle";
    private const string CK_PollJitterSeconds = "Evolution:PollJitterSeconds";
    private const string CK_LockTimeoutSeconds = "Evolution:LockTimeoutSeconds";

    private const string DistributedLockKey = "workers:evolutionary-generator:cycle";
    internal const string FleetSystemicDedupeKey = "Evolution:FleetSystemic";
    internal const string StalenessDedupeKey = "Evolution:Staleness";

    private const int DefaultPollSecs = 24 * 60 * 60; // daily
    private const int MinPollSecs = 60;
    private const int MaxPollSecs = 7 * 24 * 60 * 60;
    private const int DefaultMaxOffspring = 12;
    private const int MinMaxOffspring = 0;
    private const int MaxMaxOffspring = 100;
    private const int InitialValidationLookbackDays = 365;
    private const decimal InitialValidationBalance = 10_000m;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);

    private readonly ILogger<EvolutionaryGeneratorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;
    private readonly IDatabaseExceptionClassifier? _dbExceptionClassifier;
    private readonly EvolutionaryGeneratorOptions _options;

    private int _consecutiveFailures;
    private int _consecutiveZeroInsertCycles;
    private bool _missingDistributedLockWarningEmitted;
    private bool _fleetSystemicAlertActive;
    private bool _stalenessAlertActive;

    public EvolutionaryGeneratorWorker(
        ILogger<EvolutionaryGeneratorWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null,
        IDatabaseExceptionClassifier? dbExceptionClassifier = null,
        EvolutionaryGeneratorOptions? options = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
        _dbExceptionClassifier = dbExceptionClassifier;
        _options = options ?? new EvolutionaryGeneratorOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Generates canonicalized evolutionary offspring, persists them as draft strategies, and queues their initial validation backtests.",
            TimeSpan.FromSeconds(DefaultPollSecs));

        var currentPollInterval = TimeSpan.FromSeconds(DefaultPollSecs);

        try
        {
            try
            {
                var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName);
                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                long cycleStarted = Stopwatch.GetTimestamp();

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                    var result = await RunCycleAsync(stoppingToken);
                    currentPollInterval = result.Settings.PollInterval;

                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.BacklogDepth);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.EvolutionaryCycleDurationMs.Record(durationMs);

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else if (result.InsertedCandidateCount > 0 || result.PersistenceFailureCount > 0)
                    {
                        _logger.LogInformation(
                            "{Worker}: proposed={Proposed}, inserted={Inserted}, queuedBacktests={Queued}, skipped={Skipped}, persistenceFailures={Failures}, backlog={Backlog}.",
                            WorkerName,
                            result.ProposedCandidateCount,
                            result.InsertedCandidateCount,
                            result.QueuedBacktestCount,
                            result.SkippedCandidateCount,
                            result.PersistenceFailureCount,
                            result.BacklogDepth);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "{Worker}: no new evolutionary offspring persisted (proposed={Proposed}, skipped={Skipped}, backlog={Backlog}).",
                            WorkerName,
                            result.ProposedCandidateCount,
                            result.SkippedCandidateCount,
                            result.BacklogDepth);
                    }

                    if (_consecutiveFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveFailures);
                        _logger.LogInformation(
                            "{Worker}: recovered after {Failures} consecutive failure(s).",
                            WorkerName,
                            _consecutiveFailures);
                    }

                    _consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _metrics?.WorkerErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("reason", "evolutionary_generator_cycle"));
                    _metrics?.EvolutionaryConsecutiveCycleFailures.Add(_consecutiveFailures);
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                try
                {
                    // Anti-lockstep: jitter the delay so two replicas started together
                    // don't race on the cycle-level distributed lock at the same instant.
                    await Task.Delay(
                        ApplyJitter(
                            CalculateDelay(currentPollInterval, _consecutiveFailures),
                            _options.PollJitterSeconds),
                        _timeProvider,
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<EvolutionaryGeneratorCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var writeContext = serviceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var generator = serviceProvider.GetRequiredService<IEvolutionaryStrategyGenerator>();
        var validationRunFactory = serviceProvider.GetRequiredService<IValidationRunFactory>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (_distributedLock is null)
        {
            _metrics?.EvolutionaryLockAttempts.Add(
                1, new KeyValuePair<string, object?>("outcome", "unavailable"));
            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate evolutionary cycles are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var lockTimeout = TimeSpan.FromSeconds(settings.LockTimeoutSeconds);
            var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, lockTimeout, ct);
            if (cycleLock is null)
            {
                _metrics?.EvolutionaryLockAttempts.Add(
                    1, new KeyValuePair<string, object?>("outcome", "busy"));
                int busyBacklog = await CountPendingValidationBacklogAsync(db, ct);
                return EvolutionaryGeneratorCycleResult.Skipped(settings, busyBacklog, "lock_busy");
            }

            _metrics?.EvolutionaryLockAttempts.Add(
                1, new KeyValuePair<string, object?>("outcome", "acquired"));
            await using (cycleLock)
            {
                return await RunCycleCoreAsync(db, writeContext, generator, validationRunFactory, settings, ct);
            }
        }

        return await RunCycleCoreAsync(db, writeContext, generator, validationRunFactory, settings, ct);
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(DefaultPollSecs) : baseInterval;

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private async Task<EvolutionaryGeneratorCycleResult> RunCycleCoreAsync(
        DbContext db,
        IWriteApplicationDbContext writeContext,
        IEvolutionaryStrategyGenerator generator,
        IValidationRunFactory validationRunFactory,
        EvolutionaryGeneratorSettings settings,
        CancellationToken ct)
    {
        int backlogBefore = await CountPendingValidationBacklogAsync(db, ct);
        if (!settings.Enabled)
            return EvolutionaryGeneratorCycleResult.Skipped(settings, backlogBefore, "disabled");

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        string cycleId = BuildCycleId(nowUtc);

        // Phase A: proposal generation.
        var proposalStart = Stopwatch.GetTimestamp();
        var proposedCandidates = await generator.ProposeOffspringAsync(settings.MaxOffspring, ct);
        _metrics?.EvolutionaryPhaseProposalMs.Record(Stopwatch.GetElapsedTime(proposalStart).TotalMilliseconds);
        _metrics?.EvolutionaryCandidatesProposed.Add(proposedCandidates.Count);

        if (proposedCandidates.Count == 0)
        {
            // Even on empty cycles, run the staleness check — a worker that's never
            // proposing anything for a week needs operator visibility.
            await UpdateStalenessAlertAsync(db, settings, nowUtc, ct);
            return EvolutionaryGeneratorCycleResult.Empty(settings, backlogBefore, proposedCandidates.Count);
        }

        var eligibleParents = await LoadEligibleParentsAsync(
            db,
            proposedCandidates
                .Select(candidate => candidate.ParentStrategyId)
                .Distinct()
                .ToArray(),
            ct);

        var preparedCandidates = new List<PreparedEvolutionaryCandidate>(proposedCandidates.Count);
        var preparedKeys = new HashSet<CandidateKey>();

        int ineligibleParentCount = 0;
        int invalidParameterCount = 0;
        int duplicateProposalCount = 0;

        foreach (var proposed in proposedCandidates)
        {
            if (!eligibleParents.TryGetValue(proposed.ParentStrategyId, out var parent))
            {
                ineligibleParentCount++;
                continue;
            }

            if (!TryPrepareCandidate(proposed, parent, cycleId, out var prepared))
            {
                invalidParameterCount++;
                continue;
            }

            if (!preparedKeys.Add(prepared.Key))
            {
                duplicateProposalCount++;
                continue;
            }

            preparedCandidates.Add(prepared);
        }

        // Phase B: dedupe load (existing strategies + active validation-queue keys).
        var dedupeLoadStart = Stopwatch.GetTimestamp();
        var existingStrategyKeys = await LoadExistingStrategyKeysAsync(db, preparedCandidates, ct);
        var activeValidationQueueKeys = await LoadActiveValidationQueueKeysAsync(
            db,
            preparedCandidates
                .Select(candidate => candidate.BacktestQueueKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            ct);
        _metrics?.EvolutionaryPhaseDedupeLoadMs.Record(Stopwatch.GetElapsedTime(dedupeLoadStart).TotalMilliseconds);

        int existingStrategyCount = 0;
        int activeQueueCount = 0;
        int insertedCount = 0;
        int queuedBacktestCount = 0;
        int persistenceFailureCount = 0;

        // Phase C: persistence (per-candidate transactions).
        var persistenceStart = Stopwatch.GetTimestamp();
        foreach (var candidate in preparedCandidates)
        {
            ct.ThrowIfCancellationRequested();

            if (existingStrategyKeys.Contains(candidate.Key))
            {
                existingStrategyCount++;
                continue;
            }

            if (activeValidationQueueKeys.Contains(candidate.BacktestQueueKey))
            {
                activeQueueCount++;
                continue;
            }

            if (!await TryPersistCandidateAsync(db, writeContext, validationRunFactory, candidate, nowUtc, ct))
            {
                persistenceFailureCount++;
                continue;
            }

            insertedCount++;
            queuedBacktestCount++;
            existingStrategyKeys.Add(candidate.Key);
            activeValidationQueueKeys.Add(candidate.BacktestQueueKey);
        }

        _metrics?.EvolutionaryPhasePersistenceMs.Record(Stopwatch.GetElapsedTime(persistenceStart).TotalMilliseconds);

        RecordSkippedCandidateMetrics("parent_ineligible", ineligibleParentCount);
        RecordSkippedCandidateMetrics("invalid_parameters", invalidParameterCount);
        RecordSkippedCandidateMetrics("duplicate_proposal", duplicateProposalCount);
        RecordSkippedCandidateMetrics("existing_strategy", existingStrategyCount);
        RecordSkippedCandidateMetrics("active_validation_queue", activeQueueCount);
        RecordSkippedCandidateMetrics("persist_failed", persistenceFailureCount);

        if (insertedCount > 0)
            _metrics?.EvolutionaryCandidatesInserted.Add(insertedCount);

        if (queuedBacktestCount > 0)
            _metrics?.EvolutionaryBacktestsQueued.Add(queuedBacktestCount);

        int backlogDepth = await CountPendingValidationBacklogAsync(db, ct);

        var result = new EvolutionaryGeneratorCycleResult(
            settings,
            ProposedCandidateCount: proposedCandidates.Count,
            InsertedCandidateCount: insertedCount,
            QueuedBacktestCount: queuedBacktestCount,
            DuplicateProposalCount: duplicateProposalCount,
            ExistingStrategyCount: existingStrategyCount,
            ActiveQueueCount: activeQueueCount,
            IneligibleParentCount: ineligibleParentCount,
            InvalidParameterCount: invalidParameterCount,
            PersistenceFailureCount: persistenceFailureCount,
            BacklogDepth: backlogDepth,
            SkippedReason: null);

        // Phase D: aggregate signals — fleet-systemic alert when consecutive cycles fail
        // to insert despite proposing, staleness alert when no draft has landed for too
        // long. Both auto-resolve.
        await UpdateFleetSystemicAlertAsync(result, settings, nowUtc, ct);
        await UpdateStalenessAlertAsync(db, settings, nowUtc, ct);

        return result;
    }

    private void RecordSkippedCandidateMetrics(string reason, int count)
    {
        if (count <= 0)
            return;

        _metrics?.EvolutionaryCandidatesSkipped.Add(
            count,
            new KeyValuePair<string, object?>("reason", reason));
    }

    private async Task<bool> TryPersistCandidateAsync(
        DbContext db,
        IWriteApplicationDbContext writeContext,
        IValidationRunFactory validationRunFactory,
        PreparedEvolutionaryCandidate candidate,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            var strategy = new Strategy
            {
                Name = BuildStrategyName(candidate),
                Description = $"Evolutionary offspring of strategy {candidate.ParentStrategyId}: {candidate.MutationDescription}",
                Symbol = candidate.Symbol,
                Timeframe = candidate.Timeframe,
                StrategyType = candidate.StrategyType,
                ParametersJson = candidate.ParametersJson,
                Status = StrategyStatus.Paused,
                LifecycleStage = StrategyLifecycleStage.Draft,
                LifecycleStageEnteredAt = nowUtc,
                CreatedAt = nowUtc,
                ParentStrategyId = candidate.ParentStrategyId,
                Generation = candidate.Generation,
                GenerationCycleId = candidate.CycleId,
                GenerationCandidateId = candidate.CandidateId,
                ValidationPriority = candidate.ValidationPriority,
            };

            db.Set<Strategy>().Add(strategy);
            await writeContext.SaveChangesAsync(ct);

            var backtestRun = await validationRunFactory.BuildBacktestRunAsync(
                db,
                new BacktestQueueRequest(
                    StrategyId: strategy.Id,
                    Symbol: strategy.Symbol,
                    Timeframe: strategy.Timeframe,
                    FromDate: nowUtc.AddDays(-InitialValidationLookbackDays),
                    ToDate: nowUtc,
                    InitialBalance: InitialValidationBalance,
                    QueueSource: ValidationRunQueueSources.StrategyGenerationInitial,
                    Priority: strategy.ValidationPriority,
                    ParametersSnapshotJson: strategy.ParametersJson,
                    ValidationQueueKey: candidate.BacktestQueueKey),
                ct);

            db.Set<BacktestRun>().Add(backtestRun);
            await writeContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation must propagate cleanly — the cycle is shutting down. No
            // rollback attempt (the disposal of the transaction handles it) and no metric
            // increment, so the cancel doesn't pollute the persistence-failure counter.
            DetachDirtyEntries(db);
            throw;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // A concurrent worker (or earlier replica) already inserted a row that hits the
            // unique index — typically the BacktestRun.ValidationQueueKey constraint. The
            // candidate is already covered downstream, so this is a no-op, not a failure.
            try { await transaction.RollbackAsync(ct); } catch { /* best effort */ }
            DetachDirtyEntries(db);
            _metrics?.EvolutionaryUniqueConstraintRaces.Add(1);
            _logger.LogInformation(
                "{Worker}: unique-constraint race for candidate {CandidateId} (parent {ParentStrategyId}); another writer already covered it.",
                WorkerName, candidate.CandidateId, candidate.ParentStrategyId);
            return false;
        }
        catch (Exception ex)
        {
            try
            {
                await transaction.RollbackAsync(ct);
            }
            catch
            {
                // Best effort rollback only.
            }

            DetachDirtyEntries(db);
            _logger.LogWarning(
                ex,
                "{Worker}: failed to persist evolutionary candidate {CandidateId} for parent {ParentStrategyId}.",
                WorkerName,
                candidate.CandidateId,
                candidate.ParentStrategyId);
            return false;
        }
    }

    private async Task<EvolutionaryGeneratorSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        bool enabled = await GetBoolAsync(db, CK_Enabled, defaultValue: _options.Enabled, ct);
        int configuredPollSeconds = await GetIntAsync(db, CK_PollSeconds, _options.PollIntervalSeconds, ct);
        int configuredMaxOffspring = await GetIntAsync(db, CK_MaxOffspring, _options.MaxOffspringPerCycle, ct);
        int configuredPollJitterSeconds = await GetIntAsync(db, CK_PollJitterSeconds, _options.PollJitterSeconds, ct);
        int configuredLockTimeoutSeconds = await GetIntAsync(db, CK_LockTimeoutSeconds, _options.LockTimeoutSeconds, ct);

        int pollSeconds = Clamp(configuredPollSeconds, MinPollSecs, MaxPollSecs);
        int maxOffspring = Clamp(configuredMaxOffspring, MinMaxOffspring, MaxMaxOffspring);
        int pollJitterSeconds = Clamp(configuredPollJitterSeconds, 0, 86_400);
        int lockTimeoutSeconds = Clamp(configuredLockTimeoutSeconds, 0, 300);

        if (configuredPollSeconds != pollSeconds)
        {
            _logger.LogDebug(
                "{Worker}: clamped invalid poll interval {Configured}s to {Effective}s.",
                WorkerName,
                configuredPollSeconds,
                pollSeconds);
        }

        if (configuredMaxOffspring != maxOffspring)
        {
            _logger.LogDebug(
                "{Worker}: clamped invalid max offspring {Configured} to {Effective}.",
                WorkerName,
                configuredMaxOffspring,
                maxOffspring);
        }

        return new EvolutionaryGeneratorSettings(
            Enabled: enabled,
            PollInterval: TimeSpan.FromSeconds(pollSeconds),
            MaxOffspring: maxOffspring,
            PollJitterSeconds: pollJitterSeconds,
            LockTimeoutSeconds: lockTimeoutSeconds,
            FailureBackoffCapShift: Clamp(_options.FailureBackoffCapShift, 0, 16),
            FleetSystemicConsecutiveZeroInsertCycles: Math.Max(1, _options.FleetSystemicConsecutiveZeroInsertCycles),
            StalenessAlertHours: Math.Max(1, _options.StalenessAlertHours));
    }

    private async Task<Dictionary<long, EligibleParentInfo>> LoadEligibleParentsAsync(
        DbContext db,
        IReadOnlyCollection<long> parentIds,
        CancellationToken ct)
    {
        if (parentIds.Count == 0)
            return [];

        var parents = await db.Set<Strategy>()
            .AsNoTracking()
            .Where(strategy =>
                !strategy.IsDeleted &&
                parentIds.Contains(strategy.Id) &&
                (strategy.Status == StrategyStatus.Active || strategy.LifecycleStage == StrategyLifecycleStage.Approved))
            .Select(strategy => new
            {
                strategy.Id,
                strategy.Symbol,
                strategy.Timeframe,
                strategy.StrategyType,
                strategy.Generation
            })
            .ToListAsync(ct);

        var sharpeRows = await db.Set<StrategyPerformanceSnapshot>()
            .AsNoTracking()
            .Where(snapshot => !snapshot.IsDeleted && parentIds.Contains(snapshot.StrategyId))
            .OrderByDescending(snapshot => snapshot.EvaluatedAt)
            .Select(snapshot => new
            {
                snapshot.StrategyId,
                snapshot.SharpeRatio
            })
            .ToListAsync(ct);

        var latestSharpeByParentId = sharpeRows
            .GroupBy(snapshot => snapshot.StrategyId)
            .ToDictionary(
                group => group.Key,
                group => (decimal?)group.First().SharpeRatio);

        return parents.ToDictionary(
            parent => parent.Id,
            parent => new EligibleParentInfo(
                parent.Id,
                NormalizeSymbol(parent.Symbol),
                parent.Timeframe,
                parent.StrategyType,
                parent.Generation,
                latestSharpeByParentId.GetValueOrDefault(parent.Id)));
    }

    private async Task<HashSet<CandidateKey>> LoadExistingStrategyKeysAsync(
        DbContext db,
        IReadOnlyCollection<PreparedEvolutionaryCandidate> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
            return [];

        var strategyTypes = candidates.Select(candidate => candidate.StrategyType).Distinct().ToList();
        var symbols = candidates.Select(candidate => candidate.Symbol).Distinct(StringComparer.Ordinal).ToList();
        var timeframes = candidates.Select(candidate => candidate.Timeframe).Distinct().ToList();

        var existingStrategies = await db.Set<Strategy>()
            .AsNoTracking()
            .Where(strategy =>
                !strategy.IsDeleted &&
                strategyTypes.Contains(strategy.StrategyType) &&
                symbols.Contains(strategy.Symbol) &&
                timeframes.Contains(strategy.Timeframe))
            .Select(strategy => new
            {
                strategy.StrategyType,
                strategy.Symbol,
                strategy.Timeframe,
                strategy.ParametersJson
            })
            .ToListAsync(ct);

        var keys = new HashSet<CandidateKey>();
        foreach (var existing in existingStrategies)
        {
            if (!TryNormalizeParametersJson(existing.ParametersJson, out var normalizedParameters))
                continue;

            keys.Add(new CandidateKey(
                existing.StrategyType,
                NormalizeSymbol(existing.Symbol),
                existing.Timeframe,
                normalizedParameters));
        }

        return keys;
    }

    private static async Task<HashSet<string>> LoadActiveValidationQueueKeysAsync(
        DbContext db,
        IReadOnlyCollection<string> queueKeys,
        CancellationToken ct)
    {
        if (queueKeys.Count == 0)
            return [];

        var activeQueueKeys = await db.Set<BacktestRun>()
            .AsNoTracking()
            .Where(run =>
                !run.IsDeleted &&
                run.ValidationQueueKey != null &&
                queueKeys.Contains(run.ValidationQueueKey) &&
                (run.Status == RunStatus.Queued || run.Status == RunStatus.Running))
            .Select(run => run.ValidationQueueKey!)
            .ToListAsync(ct);

        return activeQueueKeys.ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<int> CountPendingValidationBacklogAsync(DbContext db, CancellationToken ct)
    {
        return await db.Set<Strategy>()
            .AsNoTracking()
            .Where(strategy =>
                !strategy.IsDeleted &&
                strategy.ParentStrategyId != null &&
                strategy.Status == StrategyStatus.Paused &&
                strategy.LifecycleStage == StrategyLifecycleStage.Draft)
            .CountAsync(
                strategy => !db.Set<BacktestRun>().Any(run =>
                    !run.IsDeleted &&
                    run.StrategyId == strategy.Id &&
                    run.Status == RunStatus.Completed),
                ct);
    }

    private static bool TryPrepareCandidate(
        EvolutionaryCandidate candidate,
        EligibleParentInfo parent,
        string cycleId,
        out PreparedEvolutionaryCandidate prepared)
    {
        if (!TryNormalizeParametersJson(candidate.ParametersJson, out var normalizedParameters))
        {
            prepared = default;
            return false;
        }

        var key = new CandidateKey(
            parent.StrategyType,
            parent.Symbol,
            parent.Timeframe,
            normalizedParameters);
        string candidateId = BuildCandidateId(key);

        prepared = new PreparedEvolutionaryCandidate(
            Key: key,
            CandidateId: candidateId,
            CycleId: cycleId,
            BacktestQueueKey: BuildInitialBacktestQueueKey(candidateId),
            ParentStrategyId: parent.Id,
            Generation: parent.Generation + 1,
            StrategyType: parent.StrategyType,
            Symbol: parent.Symbol,
            Timeframe: parent.Timeframe,
            ParametersJson: normalizedParameters,
            MutationDescription: string.IsNullOrWhiteSpace(candidate.MutationDescription) ? "mutation" : candidate.MutationDescription.Trim(),
            ValidationPriority: ResolveValidationPriority(parent.LatestSharpeRatio));
        return true;
    }

    private static bool TryNormalizeParametersJson(string parametersJson, out string normalizedParameters)
    {
        normalizedParameters = string.Empty;
        if (string.IsNullOrWhiteSpace(parametersJson))
            return false;

        try
        {
            if (JsonNode.Parse(parametersJson) is not JsonObject)
                return false;
        }
        catch
        {
            return false;
        }

        normalizedParameters = StrategyGenerationHelpers.NormalizeTemplateParameters(parametersJson);
        return !string.IsNullOrWhiteSpace(normalizedParameters);
    }

    private static int ResolveValidationPriority(decimal? latestSharpeRatio)
    {
        if (!latestSharpeRatio.HasValue)
            return 0;

        return Math.Max(
            0,
            (int)Math.Round((double)latestSharpeRatio.Value * 100d, MidpointRounding.AwayFromZero));
    }

    private static string BuildStrategyName(PreparedEvolutionaryCandidate candidate)
    {
        string hashSuffix = candidate.CandidateId.Length > 12
            ? candidate.CandidateId[^12..]
            : candidate.CandidateId;

        return $"evo_{candidate.Symbol}_{candidate.Timeframe}_{hashSuffix}";
    }

    private static string BuildCycleId(DateTime nowUtc)
        => $"evo-{nowUtc:yyyyMMddHHmmssfff}";

    private static string BuildInitialBacktestQueueKey(string candidateId)
        => $"strategy-candidate:{candidateId}:backtest:initial";

    private static string BuildCandidateId(CandidateKey key)
    {
        string rawIdentity = $"{key.StrategyType}|{key.Symbol}|{key.Timeframe}|{key.ParametersJson}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawIdentity)));
        return $"evo:{key.StrategyType}:{key.Symbol}:{key.Timeframe}:{hash[..24]}";
    }

    private static string NormalizeSymbol(string symbol)
        => (symbol ?? string.Empty).Trim().ToUpperInvariant();

    private static void DetachDirtyEntries(DbContext db)
    {
        var dirtyEntries = db.ChangeTracker.Entries()
            .Where(entry => entry.State != EntityState.Unchanged)
            .ToList();

        foreach (var entry in dirtyEntries)
            entry.State = EntityState.Detached;
    }

    private static async Task<int> GetIntAsync(DbContext db, string key, int fallback, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => !config.IsDeleted && config.Key == key)
            .Select(config => config.Value)
            .FirstOrDefaultAsync(ct);

        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static async Task<bool> GetBoolAsync(DbContext db, string key, bool defaultValue, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => !config.IsDeleted && config.Key == key)
            .Select(config => config.Value)
            .FirstOrDefaultAsync(ct);

        if (raw is null)
            return defaultValue;

        if (bool.TryParse(raw, out var boolValue))
            return boolValue;

        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intValue))
            return intValue != 0;

        return defaultValue;
    }

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    private readonly record struct EligibleParentInfo(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        StrategyType StrategyType,
        int Generation,
        decimal? LatestSharpeRatio);

    private readonly record struct CandidateKey(
        StrategyType StrategyType,
        string Symbol,
        Timeframe Timeframe,
        string ParametersJson);

    private readonly record struct PreparedEvolutionaryCandidate(
        CandidateKey Key,
        string CandidateId,
        string CycleId,
        string BacktestQueueKey,
        long ParentStrategyId,
        int Generation,
        StrategyType StrategyType,
        string Symbol,
        Timeframe Timeframe,
        string ParametersJson,
        string MutationDescription,
        int ValidationPriority);

    internal readonly record struct EvolutionaryGeneratorSettings(
        bool Enabled,
        TimeSpan PollInterval,
        int MaxOffspring,
        int PollJitterSeconds,
        int LockTimeoutSeconds,
        int FailureBackoffCapShift,
        int FleetSystemicConsecutiveZeroInsertCycles,
        int StalenessAlertHours);

    internal readonly record struct EvolutionaryGeneratorCycleResult(
        EvolutionaryGeneratorSettings Settings,
        int ProposedCandidateCount,
        int InsertedCandidateCount,
        int QueuedBacktestCount,
        int DuplicateProposalCount,
        int ExistingStrategyCount,
        int ActiveQueueCount,
        int IneligibleParentCount,
        int InvalidParameterCount,
        int PersistenceFailureCount,
        int BacklogDepth,
        string? SkippedReason)
    {
        public int SkippedCandidateCount
            => DuplicateProposalCount
             + ExistingStrategyCount
             + ActiveQueueCount
             + IneligibleParentCount
             + InvalidParameterCount;

        public static EvolutionaryGeneratorCycleResult Empty(
            EvolutionaryGeneratorSettings settings,
            int backlogDepth,
            int proposedCandidateCount)
            => new(
                settings,
                ProposedCandidateCount: proposedCandidateCount,
                InsertedCandidateCount: 0,
                QueuedBacktestCount: 0,
                DuplicateProposalCount: 0,
                ExistingStrategyCount: 0,
                ActiveQueueCount: 0,
                IneligibleParentCount: 0,
                InvalidParameterCount: 0,
                PersistenceFailureCount: 0,
                BacklogDepth: backlogDepth,
                SkippedReason: null);

        public static EvolutionaryGeneratorCycleResult Skipped(
            EvolutionaryGeneratorSettings settings,
            int backlogDepth,
            string reason)
            => new(
                settings,
                ProposedCandidateCount: 0,
                InsertedCandidateCount: 0,
                QueuedBacktestCount: 0,
                DuplicateProposalCount: 0,
                ExistingStrategyCount: 0,
                ActiveQueueCount: 0,
                IneligibleParentCount: 0,
                InvalidParameterCount: 0,
                PersistenceFailureCount: 0,
                BacklogDepth: backlogDepth,
                SkippedReason: reason);
    }
}
