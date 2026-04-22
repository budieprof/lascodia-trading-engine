using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Data;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Manages the full lifecycle of signal-level A/B tests between champion and challenger
/// ML models. After <see cref="MLShadowArbiterWorker"/> promotes a model through
/// accuracy-level SPRT, this coordinator validates that the challenger actually improves
/// trading P&amp;L when deployed through the full signal pipeline.
///
/// <para><b>Signal routing:</b> uses a deterministic hash of <c>strategyId</c> to assign
/// each strategy to either champion or challenger. This ensures consistent routing
/// (same strategy always gets the same model) and prevents confounding from strategy-level
/// differences.</para>
///
/// <para><b>SPRT on P&amp;L:</b> the test uses a sequential probability ratio test on the
/// cumulative P&amp;L difference between arms, with configurable effect size δ, α, and β.</para>
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public sealed class SignalAbTestCoordinator
{
    /// <summary>
    /// In-memory cache of active A/B tests keyed by (Symbol, Timeframe).
    /// Refreshed periodically by <see cref="MLSignalAbTestWorker"/>; used at scoring
    /// time by <see cref="MLSignalScorer"/> for fast O(1) lookups.
    /// </summary>
    private readonly ConcurrentDictionary<(string Symbol, Timeframe Timeframe), ActiveAbTestEntry> _activeTests = new();

    private readonly ILogger<SignalAbTestCoordinator> _logger;
    private readonly IDistributedLock? _distributedLock;

    public SignalAbTestCoordinator(
        ILogger<SignalAbTestCoordinator> logger,
        IDistributedLock? distributedLock = null)
    {
        _logger = logger;
        _distributedLock = distributedLock;
    }

    // ── A/B test lifecycle ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a new A/B test between champion and challenger models. Called by
    /// <see cref="MLShadowArbiterWorker"/> after accuracy-level SPRT passes.
    /// The A/B test validates signal-level P&amp;L performance before full promotion.
    /// </summary>
    /// <returns>
    /// The ID of the newly created <see cref="MLSignalAbTest"/> record, or -1 if a
    /// concurrent test already exists or the max concurrent limit has been reached.
    /// </returns>
    public async Task<long> StartAbTestAsync(
        long championModelId,
        long challengerModelId,
        string symbol,
        Timeframe timeframe,
        IWriteApplicationDbContext writeContext,
        IReadApplicationDbContext readContext,
        int maxConcurrentPerSymbol,
        CancellationToken ct = default)
    {
        return await StartAbTestAsync(
            championModelId,
            challengerModelId,
            symbol,
            timeframe,
            writeContext.GetDbContext(),
            readContext.GetDbContext(),
            maxConcurrentPerSymbol,
            ct);
    }

    public async Task<long> StartAbTestAsync(
        long championModelId,
        long challengerModelId,
        string symbol,
        Timeframe timeframe,
        DbContext writeDb,
        DbContext readDb,
        int maxConcurrentPerSymbol,
        CancellationToken ct = default)
    {
        var normalizedSymbol = symbol.ToUpperInvariant();
        await using var startLock = _distributedLock is null
            ? null
            : await _distributedLock.TryAcquireAsync(
                $"ml:signal-abtest-start:{normalizedSymbol}",
                TimeSpan.FromSeconds(10),
                ct);

        if (_distributedLock is not null && startLock is null)
        {
            _logger.LogWarning(
                "Cannot start A/B test for {Symbol}/{Timeframe}: another worker is starting a test for this symbol.",
                normalizedSymbol, timeframe);
            return -1;
        }

        await using var tx = writeDb.Database.CurrentTransaction is null
            ? await writeDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        // Guard: max concurrent A/B tests per symbol
        var existingCount = await writeDb.Set<MLSignalAbTest>()
            .AsNoTracking()
            .CountAsync(c => c.Symbol == normalizedSymbol &&
                             c.Status == MLSignalAbTestStatus.Active &&
                             !c.IsDeleted, ct);

        if (existingCount >= maxConcurrentPerSymbol)
        {
            _logger.LogWarning(
                "Cannot start A/B test for {Symbol}/{Timeframe}: {Count} concurrent tests already running (max {Max})",
                normalizedSymbol, timeframe, existingCount, maxConcurrentPerSymbol);
            return -1;
        }

        var activeForPair = await writeDb.Set<MLSignalAbTest>()
            .AsNoTracking()
            .AnyAsync(c => c.Symbol == normalizedSymbol &&
                           c.Timeframe == timeframe &&
                           c.Status == MLSignalAbTestStatus.Active &&
                           !c.IsDeleted, ct);

        if (activeForPair)
        {
            _logger.LogWarning(
                "Cannot start A/B test for {Symbol}/{Timeframe}: another test is already active for this pair.",
                normalizedSymbol, timeframe);
            return -1;
        }

        // Guard: no duplicate test for same champion/challenger pair
        var existing = await writeDb.Set<MLSignalAbTest>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ChampionModelId == championModelId &&
                                      c.ChallengerModelId == challengerModelId &&
                                      c.Status == MLSignalAbTestStatus.Active &&
                                      !c.IsDeleted, ct);

        if (existing is not null)
        {
            _logger.LogWarning(
                "A/B test already exists for champion {Champion} vs challenger {Challenger}",
                championModelId, challengerModelId);
            return -1;
        }

        var abTest = new MLSignalAbTest
        {
            ChampionModelId = championModelId,
            ChallengerModelId = challengerModelId,
            Symbol = normalizedSymbol,
            Timeframe = timeframe,
            Status = MLSignalAbTestStatus.Active,
            StartedAtUtc = DateTime.UtcNow,
        };
        writeDb.Set<MLSignalAbTest>().Add(abTest);
        try
        {
            await writeDb.SaveChangesAsync(ct);
            if (tx is not null)
                await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            writeDb.Entry(abTest).State = EntityState.Detached;
            var conflictingActiveTest = await writeDb.Set<MLSignalAbTest>()
                .AsNoTracking()
                .AnyAsync(c => c.Symbol == normalizedSymbol &&
                               c.Timeframe == timeframe &&
                               c.Status == MLSignalAbTestStatus.Active &&
                               !c.IsDeleted, ct);

            if (!conflictingActiveTest)
                throw;

            _logger.LogWarning(
                ex,
                "Could not start A/B test for {Symbol}/{Timeframe}: another worker created a conflicting active test.",
                normalizedSymbol, timeframe);
            return -1;
        }

        // Register in the in-memory lookup
        var key = (normalizedSymbol, timeframe);
        var entry = new ActiveAbTestEntry(abTest.Id, championModelId, challengerModelId);
        _activeTests[key] = entry;

        _logger.LogInformation(
            "Started A/B test for {Symbol}/{Timeframe}: champion={Champion}, challenger={Challenger}",
            normalizedSymbol, timeframe, championModelId, challengerModelId);

        return abTest.Id;
    }

    /// <summary>
    /// Removes a completed or cancelled A/B test from both persistent storage and the
    /// in-memory cache.
    /// </summary>
    public async Task EndAbTestAsync(
        long championModelId,
        long challengerModelId,
        string symbol,
        Timeframe timeframe,
        IWriteApplicationDbContext writeContext,
        CancellationToken ct = default)
    {
        var writeDb = writeContext.GetDbContext();

        var activeTests = await writeDb.Set<MLSignalAbTest>()
            .Where(c => c.ChampionModelId == championModelId &&
                        c.ChallengerModelId == challengerModelId &&
                        c.Status == MLSignalAbTestStatus.Active &&
                        !c.IsDeleted)
            .ToListAsync(ct);

        foreach (var test in activeTests)
        {
            test.Status = MLSignalAbTestStatus.Completed;
            test.CompletedAtUtc = DateTime.UtcNow;
        }

        if (activeTests.Count > 0)
            await writeDb.SaveChangesAsync(ct);

        _activeTests.TryRemove((symbol.ToUpperInvariant(), timeframe), out _);

        _logger.LogInformation(
            "Ended A/B test for {Symbol}/{Timeframe}: champion={Champion}, challenger={Challenger}",
            symbol, timeframe, championModelId, challengerModelId);
    }

    // ── Signal routing ──────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to find an active A/B test for the given symbol/timeframe.
    /// Returns <c>null</c> if no test is active (normal single-model scoring proceeds).
    /// </summary>
    public ActiveAbTestEntry? GetActiveTest(string symbol, Timeframe timeframe)
        => _activeTests.TryGetValue((symbol.ToUpperInvariant(), timeframe), out var entry) ? entry : null;

    /// <summary>
    /// Routes a signal scoring request to either champion or challenger based on a
    /// deterministic hash of <paramref name="strategyId"/>. 50/50 split by default.
    /// The same strategy always gets the same model, preventing confounding from
    /// strategy-level differences.
    /// </summary>
    public long ResolveModelForSignal(
        long strategyId,
        long championModelId,
        long challengerModelId)
    {
        // Deterministic: same strategy always gets the same model
        return (strategyId % 2 == 0) ? championModelId : challengerModelId;
    }

    // ── In-memory cache refresh ─────────────────────────────────────────────

    /// <summary>
    /// Refreshes the in-memory active test cache from the database. Called periodically
    /// by <see cref="MLSignalAbTestWorker"/> to pick up new tests and remove ended ones.
    /// </summary>
    public async Task RefreshActiveCacheAsync(
        IReadApplicationDbContext readContext,
        CancellationToken ct = default)
    {
        var readDb = readContext.GetDbContext();

        var activeTests = await readDb.Set<MLSignalAbTest>()
            .AsNoTracking()
            .Where(c => c.Status == MLSignalAbTestStatus.Active && !c.IsDeleted)
            .ToListAsync(ct);

        var freshEntries = new Dictionary<(string Symbol, Timeframe Timeframe), ActiveAbTestEntry>();

        foreach (var test in activeTests)
        {
            freshEntries[(test.Symbol, test.Timeframe)] = new ActiveAbTestEntry(
                test.Id,
                test.ChampionModelId,
                test.ChallengerModelId);
        }

        // Atomic swap: remove stale entries, add fresh ones
        foreach (var key in _activeTests.Keys)
        {
            if (!freshEntries.ContainsKey(key))
                _activeTests.TryRemove(key, out _);
        }

        foreach (var (key, entry) in freshEntries)
            _activeTests[key] = entry;
    }

    // ── SPRT evaluation ─────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates the A/B test using SPRT on the cumulative P&amp;L difference between arms.
    /// <list type="bullet">
    ///   <item><b>H0:</b> E[PnL_challenger − PnL_champion] = 0 (no difference)</item>
    ///   <item><b>H1:</b> E[PnL_challenger − PnL_champion] = δ (challenger is δ better)</item>
    /// </list>
    /// Where δ = minimum meaningful P&amp;L improvement, α = 0.05, β = 0.20.
    /// Upper boundary = ln((1−β)/α), Lower boundary = ln(β/(1−α)).
    /// </summary>
    /// <param name="state">Full test state with both arms' outcomes.</param>
    /// <param name="minTradesPerArm">Minimum resolved trades per arm before SPRT can decide (default 30).</param>
    /// <param name="maxDurationDays">Maximum test duration in days before auto-resolution (default 14).</param>
    /// <returns>An <see cref="AbTestResult"/> with the decision and supporting metrics.</returns>
    public AbTestResult Evaluate(
        AbTestState state,
        int minTradesPerArm = 30,
        int maxDurationDays = 14,
        AbTestEvaluationOptions? options = null)
    {
        options = NormalizeOptions(options);
        var result = new AbTestResult
        {
            ChampionTradeCount  = state.ChampionOutcomes.Count,
            ChallengerTradeCount = state.ChallengerOutcomes.Count,
        };
        result.CovariateImbalanceScore = ComputeCovariateImbalance(
            state.ChampionOutcomes,
            state.ChallengerOutcomes);

        // ── Minimum sample guard ────────────────────────────────────────────
        if (state.ChampionOutcomes.Count < minTradesPerArm ||
            state.ChallengerOutcomes.Count < minTradesPerArm)
        {
            // Check max duration even if samples are insufficient
            if (DateTime.UtcNow - state.StartedAtUtc > TimeSpan.FromDays(maxDurationDays))
            {
                result.Decision = AbTestDecision.KeepChampion;
                result.Reason   = $"Max duration ({maxDurationDays}d) exceeded with insufficient samples " +
                                  $"(champion={state.ChampionOutcomes.Count}, challenger={state.ChallengerOutcomes.Count}). " +
                                  "Keeping champion.";
                return result;
            }

            result.Decision = AbTestDecision.Inconclusive;
            result.Reason   = $"Insufficient samples: champion={state.ChampionOutcomes.Count}, " +
                              $"challenger={state.ChallengerOutcomes.Count} (min={minTradesPerArm}).";
            return result;
        }

        // ── Compute per-arm metrics ─────────────────────────────────────────
        var champPnls = state.ChampionOutcomes.Select(o => o.Pnl).ToList();
        var challPnls = state.ChallengerOutcomes.Select(o => o.Pnl).ToList();
        var robustChampPnls = Winsorize(champPnls, options.WinsorizationQuantile);
        var robustChallPnls = Winsorize(challPnls, options.WinsorizationQuantile);

        result.ChampionAvgPnl  = champPnls.Average();
        result.ChallengerAvgPnl = challPnls.Average();
        result.ChampionSharpe  = ComputeSharpe(champPnls);
        result.ChallengerSharpe = ComputeSharpe(challPnls);

        // δ = minimum meaningful improvement = 0.5 × champion's average win size
        var champWins  = robustChampPnls.Where(p => p > 0).ToList();
        double delta   = champWins.Count > 0
            ? options.DeltaWinSizeMultiplier * champWins.Average()
            : options.MinimumEffectPnl ?? 1.0;
        if (options.MinimumEffectPnl.HasValue)
            delta = Math.Max(delta, options.MinimumEffectPnl.Value);
        if (delta < 1e-6) delta = 1.0; // Floor to prevent degenerate SPRT

        // ── SPRT ────────────────────────────────────────────────────────────
        double upperBound  = Math.Log((1.0 - options.Beta) / options.Alpha);
        double lowerBound  = Math.Log(options.Beta / (1.0 - options.Alpha));
        var imbalanceAdjusted = result.CovariateImbalanceScore > options.MaxCovariateImbalance;
        if (imbalanceAdjusted)
        {
            upperBound *= options.ImbalanceEvidenceMultiplier;
            lowerBound *= options.ImbalanceEvidenceMultiplier;
        }

        double llr = ComputeSprtLogLikelihoodRatio(robustChampPnls, robustChallPnls, delta);
        result.SprtLogLikelihoodRatio = llr;

        if (llr >= upperBound)
        {
            result.Decision = AbTestDecision.PromoteChallenger;
            result.Reason   = $"SPRT crossed upper boundary ({llr:F3} >= {upperBound:F3}). " +
                              $"Challenger avg P&L={result.ChallengerAvgPnl:F4} vs Champion={result.ChampionAvgPnl:F4}. " +
                              $"Challenger Sharpe={result.ChallengerSharpe:F3} vs Champion={result.ChampionSharpe:F3}. " +
                              $"Covariate imbalance={result.CovariateImbalanceScore:F3}.";
        }
        else if (llr <= lowerBound)
        {
            result.Decision = AbTestDecision.KeepChampion;
            result.Reason   = $"SPRT crossed lower boundary ({llr:F3} <= {lowerBound:F3}). " +
                              $"Champion avg P&L={result.ChampionAvgPnl:F4} vs Challenger={result.ChallengerAvgPnl:F4}. " +
                              $"Champion Sharpe={result.ChampionSharpe:F3} vs Challenger={result.ChallengerSharpe:F3}. " +
                              $"Covariate imbalance={result.CovariateImbalanceScore:F3}.";
        }
        else if (DateTime.UtcNow - state.StartedAtUtc > TimeSpan.FromDays(maxDurationDays))
        {
            result.Decision = AbTestDecision.KeepChampion;
            result.Reason   = $"Max duration ({maxDurationDays}d) exceeded without SPRT convergence " +
                              $"(LLR={llr:F3}, bounds=[{lowerBound:F3}, {upperBound:F3}]). Keeping champion.";
        }
        else
        {
            result.Decision = AbTestDecision.Inconclusive;
            result.Reason   = $"SPRT inconclusive (LLR={llr:F3}, bounds=[{lowerBound:F3}, {upperBound:F3}]). " +
                              $"Champion trades={result.ChampionTradeCount}, Challenger trades={result.ChallengerTradeCount}. " +
                              $"Covariate imbalance={result.CovariateImbalanceScore:F3}.";
        }

        return result;
    }

    // ── SPRT log-likelihood ratio ───────────────────────────────────────────

    /// <summary>
    /// Computes the Sequential Probability Ratio Test log-likelihood ratio for the difference
    /// between two P&amp;L distributions.
    /// <para>
    /// Under H0: diff ~ N(0, σ²); under H1: diff ~ N(δ, σ²).
    /// LLR = n * d̄ * δ / σ² − n * δ² / (2σ²), where d̄ = mean(challenger) − mean(champion),
    /// σ² = pooled variance, n = min sample count.
    /// </para>
    /// </summary>
    public static double ComputeSprtLogLikelihoodRatio(
        List<double> championPnls,
        List<double> challengerPnls,
        double delta)
    {
        if (championPnls.Count == 0 || challengerPnls.Count == 0)
            return 0.0;

        double diffMean   = challengerPnls.Average() - championPnls.Average();
        double champVar   = Variance(championPnls);
        double challVar   = Variance(challengerPnls);
        double pooledVar  = (champVar + challVar) / 2.0;

        if (pooledVar < 1e-10)
            return 0.0;

        int n = Math.Min(championPnls.Count, challengerPnls.Count);
        return n * diffMean * delta / pooledVar - n * delta * delta / (2.0 * pooledVar);
    }

    // ── Statistical helpers ─────────────────────────────────────────────────

    /// <summary>Computes the (population) variance of a list of values.</summary>
    internal static double Variance(List<double> values)
    {
        if (values.Count < 2) return 0.0;
        double mean = values.Average();
        double sumSq = 0;
        for (int i = 0; i < values.Count; i++)
        {
            double diff = values[i] - mean;
            sumSq += diff * diff;
        }
        return sumSq / values.Count;
    }

    /// <summary>
    /// Computes an annualised Sharpe ratio approximation from trade P&amp;L values.
    /// Uses √252 annualisation factor (daily frequency assumption).
    /// </summary>
    internal static double ComputeSharpe(List<double> pnls)
    {
        if (pnls.Count < 2) return 0.0;
        double mean  = pnls.Average();
        double stdev = Math.Sqrt(Variance(pnls));
        if (stdev < 1e-10) return mean > 0 ? 10.0 : -10.0; // Cap extreme values
        return (mean / stdev) * Math.Sqrt(252.0);
    }

    private static AbTestEvaluationOptions NormalizeOptions(AbTestEvaluationOptions? options)
    {
        options ??= new AbTestEvaluationOptions();

        var alpha = options.Alpha is > 0 and < 1 ? options.Alpha : 0.05;
        var beta = options.Beta is > 0 and < 1 ? options.Beta : 0.20;
        var multiplier = options.DeltaWinSizeMultiplier > 0 ? options.DeltaWinSizeMultiplier : 0.5;
        var minimumEffect = options.MinimumEffectPnl is > 0 ? options.MinimumEffectPnl : null;
        var winsor = options.WinsorizationQuantile is >= 0 and < 0.5
            ? options.WinsorizationQuantile
            : 0.05;
        var maxImbalance = options.MaxCovariateImbalance is >= 0 and <= 1
            ? options.MaxCovariateImbalance
            : 0.35;
        var evidenceMultiplier = options.ImbalanceEvidenceMultiplier >= 1
            ? options.ImbalanceEvidenceMultiplier
            : 1.5;

        return new AbTestEvaluationOptions(
            alpha,
            beta,
            multiplier,
            minimumEffect,
            winsor,
            maxImbalance,
            evidenceMultiplier);
    }

    private static List<double> Winsorize(List<double> values, double quantile)
    {
        if (values.Count < 4 || quantile <= 0)
            return values;

        var ordered = values.OrderBy(x => x).ToList();
        var lower = ordered[(int)Math.Floor((ordered.Count - 1) * quantile)];
        var upper = ordered[(int)Math.Ceiling((ordered.Count - 1) * (1.0 - quantile))];

        return values.Select(v => Math.Min(Math.Max(v, lower), upper)).ToList();
    }

    private static double ComputeCovariateImbalance(
        IReadOnlyList<AbTestOutcome> championOutcomes,
        IReadOnlyList<AbTestOutcome> challengerOutcomes)
    {
        var strategyImbalance = DistributionDistance(
            championOutcomes
                .Where(x => x.StrategyId.HasValue)
                .Select(x => x.StrategyId!.Value),
            challengerOutcomes
                .Where(x => x.StrategyId.HasValue)
                .Select(x => x.StrategyId!.Value));

        var sessionImbalance = DistributionDistance(
            championOutcomes
                .Where(x => x.SessionHourUtc.HasValue)
                .Select(x => x.SessionHourUtc!.Value),
            challengerOutcomes
                .Where(x => x.SessionHourUtc.HasValue)
                .Select(x => x.SessionHourUtc!.Value));

        return Math.Max(strategyImbalance, sessionImbalance);
    }

    private static double DistributionDistance<T>(
        IEnumerable<T> championValues,
        IEnumerable<T> challengerValues)
        where T : notnull
    {
        var championCounts = championValues
            .GroupBy(x => x)
            .ToDictionary(g => g.Key, g => g.Count());
        var challengerCounts = challengerValues
            .GroupBy(x => x)
            .ToDictionary(g => g.Key, g => g.Count());

        var championTotal = championCounts.Values.Sum();
        var challengerTotal = challengerCounts.Values.Sum();
        if (championTotal == 0 || challengerTotal == 0)
            return 0.0;

        var keys = championCounts.Keys.Concat(challengerCounts.Keys).Distinct();
        var distance = keys.Sum(key =>
        {
            var championShare = championCounts.TryGetValue(key, out var championCount)
                ? (double)championCount / championTotal
                : 0.0;
            var challengerShare = challengerCounts.TryGetValue(key, out var challengerCount)
                ? (double)challengerCount / challengerTotal
                : 0.0;
            return Math.Abs(championShare - challengerShare);
        });

        return distance / 2.0;
    }
}
