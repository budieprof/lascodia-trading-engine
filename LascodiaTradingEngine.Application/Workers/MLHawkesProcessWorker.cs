using System.Diagnostics;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Fits Hawkes process kernel parameters (mu, alpha, beta) for active symbol/timeframe
/// signal streams. The fitted kernels are consumed by <c>HawkesSignalFilter</c> to
/// suppress new signals during self-exciting burst episodes.
/// </summary>
public sealed class MLHawkesProcessWorker : BackgroundService
{
    private const string WorkerName = nameof(MLHawkesProcessWorker);
    private const string DistributedLockKey = "ml:hawkes-process:cycle";

    private const string CK_PollSecs = "MLHawkes:PollIntervalSeconds";
    private const string CK_CalibrationWindowDays = "MLHawkes:CalibrationWindowDays";
    private const string CK_MinimumFitSamples = "MLHawkes:MinimumFitSamples";
    private const string CK_MaxPairsPerCycle = "MLHawkes:MaxPairsPerCycle";
    private const string CK_MaxSignalsPerPair = "MLHawkes:MaxSignalsPerPair";
    private const string CK_MaximumBranchingRatio = "MLHawkes:MaximumBranchingRatio";
    private const string CK_OptimisationSweeps = "MLHawkes:OptimisationSweeps";
    private const string CK_SuppressMultiplier = "MLHawkes:SuppressMultiplier";
    private const string CK_LockTimeoutSecs = "MLHawkes:LockTimeoutSeconds";

    internal static readonly TimeSpan CalibrationWindow = TimeSpan.FromDays(30);
    internal const int MinimumFitSamples = 20;
    internal const int DefaultPollSeconds = 24 * 60 * 60;
    internal const int DefaultMaxPairsPerCycle = 512;
    internal const int DefaultMaxSignalsPerPair = 20_000;
    internal const int DefaultOptimisationSweeps = 80;
    internal const int DefaultLockTimeoutSeconds = 0;
    internal const double DefaultSuppressMultiplier = 2.0;

    private const double MaximumBranchingRatio = 0.95;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLHawkesProcessWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;

    public MLHawkesProcessWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLHawkesProcessWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLHawkesProcessWorker started.");
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Fits per symbol/timeframe Hawkes signal-clustering kernels.",
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName));
                _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                _logger.LogError(ex, "MLHawkesProcessWorker error");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(pollSecs, 300, 7 * 24 * 60 * 60)), stoppingToken);
        }

        _healthMonitor?.RecordWorkerStopped(WorkerName);
        _logger.LogInformation("MLHawkesProcessWorker stopping.");
    }

    internal async Task RunAsync(CancellationToken ct)
        => await RunCycleAsync(ct);

    internal async Task<int> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
        var config = await LoadConfigAsync(readDb, ct);

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is not null)
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(config.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _logger.LogDebug("MLHawkesProcessWorker: cycle skipped because distributed lock is held elsewhere.");
                return config.PollSeconds;
            }
        }
        else
        {
            _logger.LogWarning(
                "MLHawkesProcessWorker running without IDistributedLock; duplicate active kernel rows are possible in multi-instance deployments.");
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                await RunCycleCoreAsync(readDb, writeDb, config, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }

        return config.PollSeconds;
    }

    private async Task RunCycleCoreAsync(
        DbContext readDb,
        DbContext writeDb,
        HawkesProcessConfig config,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = nowUtc - TimeSpan.FromDays(config.CalibrationWindowDays);
        var activeContexts = await LoadActiveContextsAsync(readDb, config, ct);
        var activePairs = activeContexts.Select(c => c.Key).ToHashSet();

        _healthMonitor?.RecordBacklogDepth(WorkerName, activeContexts.Count);

        var stats = new HawkesCycleStats();
        await ReconcileInactiveAndDuplicateKernelsAsync(writeDb, activePairs, stats, ct);

        foreach (var context in activeContexts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProcessPairAsync(readDb, writeDb, context, cutoff, nowUtc, config, stats, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stats.Failed++;
                _logger.LogWarning(ex,
                    "MLHawkesProcessWorker: failed to fit {Symbol}/{Timeframe}; continuing.",
                    context.Key.Symbol, context.Key.Timeframe);
            }
        }

        _logger.LogInformation(
            "MLHawkesProcessWorker cycle complete: fitted={Fitted}, skipped={Skipped}, invalid={Invalid}, failed={Failed}, softDeleted={SoftDeleted}, pairs={Pairs}.",
            stats.Fitted, stats.Skipped, stats.Invalid, stats.Failed, stats.SoftDeleted, activeContexts.Count);
    }

    private async Task ProcessPairAsync(
        DbContext readDb,
        DbContext writeDb,
        HawkesContext context,
        DateTime cutoff,
        DateTime nowUtc,
        HawkesProcessConfig config,
        HawkesCycleStats stats,
        CancellationToken ct)
    {
        var pair = context.Key;
        var signals = await LoadSignalTimestampsAsync(readDb, context, cutoff, nowUtc, config, ct);
        if (signals.Count < config.MinimumFitSamples)
        {
            stats.Skipped++;
            stats.SoftDeleted += await SoftDeleteKernelAsync(writeDb, pair, ct);
            _logger.LogDebug(
                "Hawkes fit skipped for {Symbol}/{Timeframe}: only {Count}/{Minimum} samples.",
                pair.Symbol, pair.Timeframe, signals.Count, config.MinimumFitSamples);
            return;
        }

        var fit = FitHawkesKernel(
            signals,
            cutoff,
            nowUtc,
            config.MaximumBranchingRatio,
            config.OptimisationSweeps);

        if (!fit.IsValid)
        {
            stats.Invalid++;
            stats.SoftDeleted += await SoftDeleteKernelAsync(writeDb, pair, ct);
            _logger.LogWarning(
                "Hawkes fit rejected for {Symbol}/{Timeframe}: invalid parameters mu={Mu} alpha={Alpha} beta={Beta} LL={LL}.",
                pair.Symbol, pair.Timeframe, fit.Mu, fit.Alpha, fit.Beta, fit.LogLikelihood);
            return;
        }

        stats.SoftDeleted += await UpsertKernelAsync(writeDb, pair, fit, signals.Count, nowUtc, config, ct);
        stats.Fitted++;

        _logger.LogDebug(
            "Hawkes fit {Symbol}/{Timeframe}: mu={Mu:F4} alpha={Alpha:F4} beta={Beta:F4} ratio={Ratio:F3} LL={LL:F2}, samples={Samples}.",
            pair.Symbol, pair.Timeframe, fit.Mu, fit.Alpha, fit.Beta, fit.Alpha / fit.Beta, fit.LogLikelihood, signals.Count);
    }

    internal static HawkesFitResult FitHawkesKernel(
        IReadOnlyList<DateTime> timestamps,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        double maximumBranchingRatio = MaximumBranchingRatio,
        int optimisationSweeps = DefaultOptimisationSweeps)
    {
        if (timestamps.Count < 3 || windowEndUtc <= windowStartUtc)
            return HawkesFitResult.Invalid;

        double horizonHours = (windowEndUtc - windowStartUtc).TotalHours;
        if (!double.IsFinite(horizonHours) || horizonHours <= 0)
            return HawkesFitResult.Invalid;

        var ts = timestamps
            .Select(t => (ToUtc(t) - windowStartUtc).TotalHours)
            .Where(t => double.IsFinite(t) && t >= 0 && t <= horizonHours)
            .OrderBy(t => t)
            .ToArray();

        if (ts.Length < 3)
            return HawkesFitResult.Invalid;

        maximumBranchingRatio = Math.Clamp(maximumBranchingRatio, 0.05, 0.999);
        optimisationSweeps = Math.Clamp(optimisationSweeps, 10, 500);

        double empiricalRate = Math.Max(1e-6, ts.Length / horizonHours);
        double[] muStarts = [empiricalRate * 0.35, empiricalRate * 0.75, empiricalRate, empiricalRate * 1.5];
        double[] betaStarts = [0.05, 0.20, 0.75, 2.0, 6.0];
        double[] branchingStarts = [0.02, 0.15, 0.35, 0.65, 0.85];

        var best = HawkesFitResult.Invalid;
        foreach (double muStart in muStarts)
        foreach (double betaStart in betaStarts)
        foreach (double branchingStart in branchingStarts)
        {
            var candidate = OptimiseFromStart(
                ts,
                horizonHours,
                muStart,
                betaStart,
                branchingStart,
                maximumBranchingRatio,
                optimisationSweeps);

            if (candidate.IsValid && (!best.IsValid || candidate.LogLikelihood > best.LogLikelihood))
                best = candidate;
        }

        return best;
    }

    private static HawkesFitResult OptimiseFromStart(
        double[] ts,
        double horizonHours,
        double muStart,
        double betaStart,
        double branchingStart,
        double maximumBranchingRatio,
        int optimisationSweeps)
    {
        double logMu = Math.Log(Math.Max(1e-6, muStart));
        double logBeta = Math.Log(Math.Max(1e-6, betaStart));
        double logitBranching = Logit(Math.Clamp(branchingStart / maximumBranchingRatio, 1e-6, 1 - 1e-6));

        double[] step = [1.0, 1.0, 1.0];
        double bestLl = EvaluateTransformed(
            ts,
            horizonHours,
            logMu,
            logBeta,
            logitBranching,
            maximumBranchingRatio,
            out var best);

        for (int sweep = 0; sweep < optimisationSweeps; sweep++)
        {
            bool improved = false;
            for (int dimension = 0; dimension < 3; dimension++)
            {
                foreach (double direction in new[] { 1.0, -1.0 })
                {
                    double nextLogMu = logMu;
                    double nextLogBeta = logBeta;
                    double nextLogitBranching = logitBranching;

                    if (dimension == 0) nextLogMu += direction * step[dimension];
                    else if (dimension == 1) nextLogBeta += direction * step[dimension];
                    else nextLogitBranching += direction * step[dimension];

                    double nextLl = EvaluateTransformed(
                        ts,
                        horizonHours,
                        nextLogMu,
                        nextLogBeta,
                        nextLogitBranching,
                        maximumBranchingRatio,
                        out var candidate);

                    if (candidate.IsValid && nextLl > bestLl + 1e-8)
                    {
                        logMu = nextLogMu;
                        logBeta = nextLogBeta;
                        logitBranching = nextLogitBranching;
                        bestLl = nextLl;
                        best = candidate;
                        improved = true;
                    }
                }
            }

            if (!improved)
            {
                for (int i = 0; i < step.Length; i++)
                    step[i] *= 0.55;

                if (step.Max() < 1e-4)
                    break;
            }
        }

        return best;
    }

    private static double EvaluateTransformed(
        double[] ts,
        double horizonHours,
        double logMu,
        double logBeta,
        double logitBranching,
        double maximumBranchingRatio,
        out HawkesFitResult result)
    {
        double mu = Math.Exp(logMu);
        double beta = Math.Exp(logBeta);
        double branchingRatio = maximumBranchingRatio * Sigmoid(logitBranching);
        double alpha = beta * branchingRatio;
        double ll = EvaluateLogLikelihood(ts, horizonHours, mu, alpha, beta);

        result = double.IsFinite(ll)
            ? new HawkesFitResult(mu, alpha, beta, ll)
            : HawkesFitResult.Invalid;

        return ll;
    }

    private static double EvaluateLogLikelihood(
        double[] ts,
        double horizonHours,
        double mu,
        double alpha,
        double beta)
    {
        if (!double.IsFinite(mu) || !double.IsFinite(alpha) || !double.IsFinite(beta)
            || mu <= 0 || alpha <= 0 || beta <= 0 || alpha >= beta)
            return double.NegativeInfinity;

        double ll = 0;
        double excitation = 0;
        double previous = 0;

        for (int i = 0; i < ts.Length; i++)
        {
            double current = ts[i];
            if (i > 0)
            {
                double dt = Math.Max(0, current - previous);
                excitation = Math.Exp(-beta * dt) * (1 + excitation);
            }

            double lambda = mu + alpha * excitation;
            if (!double.IsFinite(lambda) || lambda <= 0)
                return double.NegativeInfinity;

            ll += Math.Log(lambda);
            previous = current;
        }

        double integral = mu * horizonHours;
        double excitationIntegral = 0;
        for (int i = 0; i < ts.Length; i++)
        {
            double remaining = horizonHours - ts[i];
            if (remaining < 0)
                continue;

            excitationIntegral += 1 - Math.Exp(-beta * remaining);
        }

        integral += (alpha / beta) * excitationIntegral;
        ll -= integral;

        return double.IsFinite(ll) ? ll : double.NegativeInfinity;
    }

    private static async Task<List<HawkesContext>> LoadActiveContextsAsync(
        DbContext readDb,
        HawkesProcessConfig config,
        CancellationToken ct)
    {
        var strategies = await readDb.Set<Strategy>()
            .AsNoTracking()
            .Where(s => !s.IsDeleted)
            .Select(s => new { s.Id, s.Symbol, s.Timeframe, s.Status })
            .ToListAsync(ct);

        var activeModels = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsSuppressed)
            .Select(m => new { m.Symbol, m.Timeframe })
            .ToListAsync(ct);

        var strategyIdsByPair = strategies
            .Where(p => !string.IsNullOrWhiteSpace(p.Symbol))
            .GroupBy(p => new HawkesContextKey(NormalizeSymbol(p.Symbol), p.Timeframe))
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Id).Distinct().Order().ToArray());

        var activePairs = strategies
            .Where(s => s.Status == StrategyStatus.Active && !string.IsNullOrWhiteSpace(s.Symbol))
            .Select(s => new HawkesContextKey(NormalizeSymbol(s.Symbol), s.Timeframe))
            .Concat(activeModels
                .Where(m => !string.IsNullOrWhiteSpace(m.Symbol))
                .Select(m => new HawkesContextKey(NormalizeSymbol(m.Symbol), m.Timeframe)))
            .Distinct()
            .OrderBy(p => p.Symbol)
            .ThenBy(p => p.Timeframe)
            .Take(config.MaxPairsPerCycle)
            .ToList();

        return activePairs
            .Select(pair => new HawkesContext(
                pair,
                strategyIdsByPair.TryGetValue(pair, out var ids) ? ids : []))
            .ToList();
    }

    private static async Task<List<DateTime>> LoadSignalTimestampsAsync(
        DbContext readDb,
        HawkesContext context,
        DateTime cutoff,
        DateTime nowUtc,
        HawkesProcessConfig config,
        CancellationToken ct)
    {
        if (context.StrategyIds.Count == 0)
            return [];

        var strategyIds = context.StrategyIds.ToArray();
        var signals = await readDb.Set<TradeSignal>()
            .AsNoTracking()
            .Where(s => !s.IsDeleted
                     && strategyIds.Contains(s.StrategyId)
                     && s.GeneratedAt >= cutoff
                     && s.GeneratedAt <= nowUtc)
            .OrderByDescending(s => s.GeneratedAt)
            .Take(config.MaxSignalsPerPair)
            .Select(s => s.GeneratedAt)
            .ToListAsync(ct);

        signals.Reverse();
        return signals;
    }

    private static async Task<int> UpsertKernelAsync(
        DbContext writeDb,
        HawkesContextKey pair,
        HawkesFitResult fit,
        int fitSamples,
        DateTime nowUtc,
        HawkesProcessConfig config,
        CancellationToken ct)
    {
        await using var tx = await writeDb.Database.BeginTransactionAsync(ct);
        var kernels = await writeDb.Set<MLHawkesKernelParams>()
            .Where(k => k.Timeframe == pair.Timeframe && !k.IsDeleted)
            .OrderByDescending(k => k.FittedAt)
            .ThenByDescending(k => k.Id)
            .ToListAsync(ct);

        var matchingKernels = kernels
            .Where(k => NormalizeSymbol(k.Symbol) == pair.Symbol)
            .ToList();

        var existing = matchingKernels.FirstOrDefault();
        int softDeleted = 0;
        foreach (var duplicate in matchingKernels.Skip(1))
        {
            duplicate.IsDeleted = true;
            softDeleted++;
        }

        if (existing is not null)
        {
            existing.Symbol = pair.Symbol;
            existing.Timeframe = pair.Timeframe;
            existing.Mu = fit.Mu;
            existing.Alpha = fit.Alpha;
            existing.Beta = fit.Beta;
            existing.LogLikelihood = fit.LogLikelihood;
            existing.SuppressMultiplier = config.SuppressMultiplier;
            existing.FitSamples = fitSamples;
            existing.FittedAt = nowUtc;
        }
        else
        {
            writeDb.Set<MLHawkesKernelParams>().Add(new MLHawkesKernelParams
            {
                Symbol = pair.Symbol,
                Timeframe = pair.Timeframe,
                Mu = fit.Mu,
                Alpha = fit.Alpha,
                Beta = fit.Beta,
                LogLikelihood = fit.LogLikelihood,
                SuppressMultiplier = config.SuppressMultiplier,
                FitSamples = fitSamples,
                FittedAt = nowUtc
            });
        }

        await writeDb.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return softDeleted;
    }

    private static async Task ReconcileInactiveAndDuplicateKernelsAsync(
        DbContext writeDb,
        IReadOnlySet<HawkesContextKey> activePairs,
        HawkesCycleStats stats,
        CancellationToken ct)
    {
        var kernels = await writeDb.Set<MLHawkesKernelParams>()
            .Where(k => !k.IsDeleted)
            .ToListAsync(ct);

        bool changed = false;
        foreach (var kernel in kernels)
        {
            var key = new HawkesContextKey(NormalizeSymbol(kernel.Symbol), kernel.Timeframe);
            if (activePairs.Contains(key))
                continue;

            kernel.IsDeleted = true;
            stats.SoftDeleted++;
            changed = true;
        }

        foreach (var duplicate in kernels
            .Where(k => !k.IsDeleted)
            .GroupBy(k => new HawkesContextKey(NormalizeSymbol(k.Symbol), k.Timeframe))
            .SelectMany(g => g.OrderByDescending(k => k.FittedAt).ThenByDescending(k => k.Id).Skip(1)))
        {
            duplicate.IsDeleted = true;
            stats.SoftDeleted++;
            changed = true;
        }

        if (changed)
            await writeDb.SaveChangesAsync(ct);
    }

    private static async Task<int> SoftDeleteKernelAsync(
        DbContext writeDb,
        HawkesContextKey pair,
        CancellationToken ct)
    {
        var kernels = await writeDb.Set<MLHawkesKernelParams>()
            .Where(k => k.Timeframe == pair.Timeframe && !k.IsDeleted)
            .ToListAsync(ct);

        kernels = kernels
            .Where(k => NormalizeSymbol(k.Symbol) == pair.Symbol)
            .ToList();

        if (kernels.Count == 0)
            return 0;

        foreach (var kernel in kernels)
            kernel.IsDeleted = true;

        await writeDb.SaveChangesAsync(ct);
        return kernels.Count;
    }

    private static async Task<HawkesProcessConfig> LoadConfigAsync(DbContext ctx, CancellationToken ct)
    {
        var values = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("MLHawkes:"))
            .Select(c => new { c.Key, c.Value })
            .ToDictionaryAsync(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase, ct);

        return new HawkesProcessConfig(
            PollSeconds: GetInt(values, CK_PollSecs, DefaultPollSeconds, 300, 7 * 24 * 60 * 60),
            CalibrationWindowDays: GetInt(values, CK_CalibrationWindowDays, (int)CalibrationWindow.TotalDays, 1, 365),
            MinimumFitSamples: GetInt(values, CK_MinimumFitSamples, MinimumFitSamples, 3, 1_000_000),
            MaxPairsPerCycle: GetInt(values, CK_MaxPairsPerCycle, DefaultMaxPairsPerCycle, 1, 100_000),
            MaxSignalsPerPair: GetInt(values, CK_MaxSignalsPerPair, DefaultMaxSignalsPerPair, MinimumFitSamples, 1_000_000),
            MaximumBranchingRatio: GetDouble(values, CK_MaximumBranchingRatio, MaximumBranchingRatio, 0.05, 0.999),
            OptimisationSweeps: GetInt(values, CK_OptimisationSweeps, DefaultOptimisationSweeps, 10, 500),
            SuppressMultiplier: GetDouble(values, CK_SuppressMultiplier, DefaultSuppressMultiplier, 1.01, 100.0),
            LockTimeoutSeconds: GetInt(values, CK_LockTimeoutSecs, DefaultLockTimeoutSeconds, 0, 300));
    }

    private static int GetInt(Dictionary<string, string> values, string key, int fallback, int min, int max)
        => values.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    private static double GetDouble(Dictionary<string, string> values, string key, double fallback, double min, double max)
        => values.TryGetValue(key, out var raw) && double.TryParse(raw, out var parsed) && double.IsFinite(parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    private static DateTime ToUtc(DateTime value)
        => value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string NormalizeSymbol(string symbol)
        => symbol.Trim().ToUpperInvariant();

    private static double Sigmoid(double value)
    {
        if (value >= 0)
        {
            double z = Math.Exp(-value);
            return 1 / (1 + z);
        }

        double exp = Math.Exp(value);
        return exp / (1 + exp);
    }

    private static double Logit(double p) => Math.Log(p / (1 - p));

    internal readonly record struct HawkesFitResult(double Mu, double Alpha, double Beta, double LogLikelihood)
    {
        public static HawkesFitResult Invalid => new(double.NaN, double.NaN, double.NaN, double.NegativeInfinity);

        public bool IsValid =>
            double.IsFinite(Mu)
            && double.IsFinite(Alpha)
            && double.IsFinite(Beta)
            && double.IsFinite(LogLikelihood)
            && Mu > 0
            && Alpha > 0
            && Beta > 0
            && Alpha < Beta;
    }

    internal sealed record HawkesProcessConfig(
        int PollSeconds,
        int CalibrationWindowDays,
        int MinimumFitSamples,
        int MaxPairsPerCycle,
        int MaxSignalsPerPair,
        double MaximumBranchingRatio,
        int OptimisationSweeps,
        double SuppressMultiplier,
        int LockTimeoutSeconds);

    private readonly record struct HawkesContextKey(string Symbol, Timeframe Timeframe);

    private sealed record HawkesContext(HawkesContextKey Key, IReadOnlyList<long> StrategyIds);

    private sealed class HawkesCycleStats
    {
        public int Fitted { get; set; }
        public int Skipped { get; set; }
        public int Invalid { get; set; }
        public int Failed { get; set; }
        public int SoftDeleted { get; set; }
    }
}
