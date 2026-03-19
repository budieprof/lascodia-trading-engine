using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Default implementation of <see cref="ITrainerSelector"/>.
/// </summary>
/// <remarks>
/// Selection pipeline (first match wins):
/// <list type="number">
///   <item><b>Regime-conditional bias:</b> when a <see cref="MarketRegimeEnum"/> is supplied,
///         the composite score of each candidate architecture is multiplied by a regime
///         affinity factor (see <see cref="RegimeAffinity"/>).  Architectures that
///         historically suit the current regime get a 20% boost; poor fits get a 20% penalty.</item>
///   <item><b>Historical performance:</b> queries the last
///         <see cref="HistoryWindowRuns"/> completed <see cref="MLTrainingRun"/>s for the
///         symbol/timeframe pair, groups by <see cref="LearnerArchitecture"/>, and picks
///         the one with the highest <b>recency-weighted composite score</b>.  The composite
///         combines <c>DirectionAccuracy</c>, <c>F1Score</c>, <c>SharpeRatio</c>, and
///         <c>ExpectedValue</c> (see <see cref="ComputeCompositeScore"/>).  Recent runs
///         are up-weighted via exponential decay with a <see cref="RecencyHalfLifeDays"/>
///         half-life.  Requires at least <see cref="MinRunsPerArchitecture"/> runs per
///         candidate architecture to be considered.</item>
///   <item><b>Operator default:</b> reads <c>MLTraining:DefaultArchitecture</c> from
///         <see cref="EngineConfig"/>.</item>
///   <item><b>Regime default:</b> a built-in default architecture for the supplied regime
///         when no history or operator config exists.</item>
///   <item><b>Fallback:</b> <see cref="LearnerArchitecture.BaggedLogistic"/>.</item>
/// </list>
/// After a candidate is chosen, a <b>three-tier sample-count gate</b> downgrades it:
/// <list type="bullet">
///   <item><see cref="SimpleTier"/> architectures are always eligible.</item>
///   <item>Standard-tier architectures require ≥ <c>MLTraining:MinSamplesForComplexModel</c>
///         samples (default 500).</item>
///   <item><see cref="DeepTier"/> architectures require ≥ <c>MLTraining:MinSamplesForDeepModel</c>
///         samples (default 2 000).</item>
/// </list>
/// Any architecture that fails its tier gate is downgraded to
/// <see cref="LearnerArchitecture.BaggedLogistic"/>.
/// </remarks>
public sealed class TrainerSelector : ITrainerSelector
{
    private const string CK_DefaultArch    = "MLTraining:DefaultArchitecture";
    private const string CK_MinSamplesStd  = "MLTraining:MinSamplesForComplexModel";
    private const string CK_MinSamplesDeep = "MLTraining:MinSamplesForDeepModel";

    private const int    HistoryWindowRuns      = 30;
    private const int    MinRunsPerArchitecture = 2;
    private const double RecencyHalfLifeDays    = 30.0;

    // Composite score weights (sum = 1.0)
    private const double WeightAccuracy = 0.35;
    private const double WeightF1       = 0.25;
    private const double WeightSharpe   = 0.25;
    private const double WeightEv       = 0.15;

    /// <summary>Regime affinity multiplier: boost (+20%) or penalty (−20%) applied to composite scores.</summary>
    private const double RegimeBoost   = 1.20;
    private const double RegimeNeutral = 1.00;
    private const double RegimePenalty = 0.80;

    // ── Tier classifications ────────────────────────────────────────────────

    /// <summary>
    /// Low-parameter architectures that generalise on small datasets and are
    /// never subject to a sample-count minimum.
    /// </summary>
    private static readonly HashSet<LearnerArchitecture> SimpleTier =
    [
        LearnerArchitecture.BaggedLogistic,
        LearnerArchitecture.Gbm,
        LearnerArchitecture.Elm,
        LearnerArchitecture.Rocket,
        LearnerArchitecture.AdaBoost,
        LearnerArchitecture.Smote,
        LearnerArchitecture.QuantileRf,
    ];

    /// <summary>
    /// High-capacity architectures that require substantially more data to avoid overfitting.
    /// These require ≥ <c>MLTraining:MinSamplesForDeepModel</c> samples.
    /// </summary>
    private static readonly HashSet<LearnerArchitecture> DeepTier =
    [
        LearnerArchitecture.TemporalConvNet,
    ];

    // ── All 12 production-grade (A+) architectures ──────────────────────────

    /// <summary>
    /// The 12 production-grade architectures eligible for primary selection and
    /// shadow rotation.
    /// </summary>
    private static readonly LearnerArchitecture[] ProductionArchitectures =
    [
        LearnerArchitecture.BaggedLogistic,
        LearnerArchitecture.Gbm,
        LearnerArchitecture.Elm,
        LearnerArchitecture.Rocket,
        LearnerArchitecture.AdaBoost,
        LearnerArchitecture.Smote,
        LearnerArchitecture.QuantileRf,
        LearnerArchitecture.FtTransformer,
        LearnerArchitecture.TabNet,
        LearnerArchitecture.Svgp,
        LearnerArchitecture.Dann,
        LearnerArchitecture.TemporalConvNet,
    ];

    // ── Model-family grouping for shadow diversity ──────────────────────────

    /// <summary>
    /// Groups production architectures into model families.  Shadow rotation
    /// always picks from a <b>different</b> family than the primary to maximise
    /// ensemble diversity.
    /// </summary>
    private enum ModelFamily
    {
        BaggedEnsemble,   // BaggedLogistic, ELM, SMOTE
        TreeBoosting,     // GBM, AdaBoost, QuantileRf
        ConvKernel,       // Rocket, TCN
        Transformer,      // FtTransformer, TabNet
        GaussianProcess,  // SVGP
        DomainAdaptation, // DANN
    }

    private static readonly Dictionary<LearnerArchitecture, ModelFamily> ArchitectureFamily = new()
    {
        [LearnerArchitecture.BaggedLogistic]  = ModelFamily.BaggedEnsemble,
        [LearnerArchitecture.Elm]             = ModelFamily.BaggedEnsemble,
        [LearnerArchitecture.Smote]           = ModelFamily.BaggedEnsemble,
        [LearnerArchitecture.Gbm]             = ModelFamily.TreeBoosting,
        [LearnerArchitecture.AdaBoost]        = ModelFamily.TreeBoosting,
        [LearnerArchitecture.QuantileRf]      = ModelFamily.TreeBoosting,
        [LearnerArchitecture.Rocket]          = ModelFamily.ConvKernel,
        [LearnerArchitecture.TemporalConvNet] = ModelFamily.ConvKernel,
        [LearnerArchitecture.FtTransformer]   = ModelFamily.Transformer,
        [LearnerArchitecture.TabNet]          = ModelFamily.Transformer,
        [LearnerArchitecture.Svgp]            = ModelFamily.GaussianProcess,
        [LearnerArchitecture.Dann]            = ModelFamily.DomainAdaptation,
    };

    // ── Regime-conditional affinity matrix ───────────────────────────────────
    //
    // Each entry maps (regime, architecture) → composite score multiplier.
    // Boost  = architecture is well-suited for the regime.
    // Penalty = architecture is a poor fit.
    // Neutral = no strong signal either way.
    //
    // Architectures not listed for a regime default to RegimeNeutral (1.0).

    private static readonly Dictionary<MarketRegimeEnum, Dictionary<LearnerArchitecture, double>> RegimeAffinity = new()
    {
        [MarketRegimeEnum.Trending] = new()
        {
            [LearnerArchitecture.Gbm]             = RegimeBoost,   // tree splits capture directional thresholds
            [LearnerArchitecture.TemporalConvNet]  = RegimeBoost,   // temporal patterns strongest in trends
            [LearnerArchitecture.AdaBoost]         = RegimeBoost,   // hard-example focus on trend-reversal fakeouts
            [LearnerArchitecture.Dann]             = RegimeBoost,   // domain-invariant features stabilise during trend shifts
            [LearnerArchitecture.FtTransformer]    = RegimeNeutral,
            [LearnerArchitecture.TabNet]           = RegimeNeutral,
            [LearnerArchitecture.BaggedLogistic]   = RegimeNeutral,
            [LearnerArchitecture.Rocket]           = RegimeNeutral,
            [LearnerArchitecture.QuantileRf]       = RegimeNeutral,
            [LearnerArchitecture.Smote]            = RegimePenalty, // imbalance handling less critical in clear trends
            [LearnerArchitecture.Elm]              = RegimePenalty, // linear readout misses non-linear momentum
            [LearnerArchitecture.Svgp]             = RegimePenalty, // GP smoothness prior fights sharp directional moves
        },
        [MarketRegimeEnum.Ranging] = new()
        {
            [LearnerArchitecture.BaggedLogistic]   = RegimeBoost,   // best calibration for symmetric probabilities
            [LearnerArchitecture.Elm]              = RegimeBoost,   // fast retraining adapts to subtle signals
            [LearnerArchitecture.Rocket]           = RegimeBoost,   // kernel features capture oscillation patterns
            [LearnerArchitecture.Svgp]             = RegimeBoost,   // GP uncertainty prevents overtrading in noise
            [LearnerArchitecture.Smote]            = RegimeNeutral,
            [LearnerArchitecture.QuantileRf]       = RegimeNeutral,
            [LearnerArchitecture.FtTransformer]    = RegimeNeutral,
            [LearnerArchitecture.TabNet]           = RegimeNeutral,
            [LearnerArchitecture.Dann]             = RegimeNeutral,
            [LearnerArchitecture.AdaBoost]         = RegimePenalty, // adaptive reweighting chases noise in ranging markets
            [LearnerArchitecture.Gbm]              = RegimePenalty, // trees overfit to noise in low-signal regimes
            [LearnerArchitecture.TemporalConvNet]  = RegimePenalty, // temporal patterns weak in ranging markets
        },
        [MarketRegimeEnum.HighVolatility] = new()
        {
            [LearnerArchitecture.FtTransformer]    = RegimeBoost,   // attention adapts feature weighting to regime shifts
            [LearnerArchitecture.TabNet]           = RegimeBoost,   // attentive selection handles shifting feature relevance
            [LearnerArchitecture.BaggedLogistic]   = RegimeBoost,   // robust calibration + conformal prediction for tail events
            [LearnerArchitecture.Dann]             = RegimeBoost,   // domain-invariant representations stabilise in vol spikes
            [LearnerArchitecture.Smote]            = RegimeBoost,   // vol spikes create directional imbalance in labels
            [LearnerArchitecture.QuantileRf]       = RegimeBoost,   // wide prediction intervals → conservative sizing in vol
            [LearnerArchitecture.Gbm]              = RegimeNeutral,
            [LearnerArchitecture.TemporalConvNet]  = RegimeNeutral,
            [LearnerArchitecture.AdaBoost]         = RegimeNeutral,
            [LearnerArchitecture.Svgp]             = RegimeNeutral,
            [LearnerArchitecture.Elm]              = RegimePenalty, // shallow architecture misses vol clustering
            [LearnerArchitecture.Rocket]           = RegimePenalty, // fixed random kernels can't adapt to vol shifts
        },
        [MarketRegimeEnum.LowVolatility] = new()
        {
            [LearnerArchitecture.Elm]              = RegimeBoost,   // fast retraining catches subtle edge
            [LearnerArchitecture.Rocket]           = RegimeBoost,   // fast inference, avoids overfitting thin signal
            [LearnerArchitecture.Svgp]             = RegimeBoost,   // high uncertainty → abstains when signal is absent
            [LearnerArchitecture.QuantileRf]       = RegimeBoost,   // narrow intervals confirm conviction before trading
            [LearnerArchitecture.BaggedLogistic]   = RegimeNeutral,
            [LearnerArchitecture.Smote]            = RegimeNeutral,
            [LearnerArchitecture.Dann]             = RegimeNeutral,
            [LearnerArchitecture.AdaBoost]         = RegimePenalty, // reweighting amplifies noise when signal is thin
            [LearnerArchitecture.Gbm]              = RegimePenalty, // trees overfit when signal-to-noise is low
            [LearnerArchitecture.FtTransformer]    = RegimePenalty, // attention has little to attend to
            [LearnerArchitecture.TabNet]           = RegimePenalty, // same — sparse attention on low-signal data
            [LearnerArchitecture.TemporalConvNet]  = RegimePenalty, // temporal patterns weak in quiet markets
        },
    };

    // ── Regime default architectures ────────────────────────────────────────

    private static readonly Dictionary<MarketRegimeEnum, LearnerArchitecture> RegimeDefault = new()
    {
        [MarketRegimeEnum.Trending]       = LearnerArchitecture.Gbm,
        [MarketRegimeEnum.Ranging]        = LearnerArchitecture.BaggedLogistic,
        [MarketRegimeEnum.HighVolatility] = LearnerArchitecture.FtTransformer,
        [MarketRegimeEnum.LowVolatility]  = LearnerArchitecture.Elm,
    };

    // ── Shadow rotation table ───────────────────────────────────────────────
    //
    // For each primary, define preferred shadow candidates ordered by priority.
    // The selector walks this list and picks the first 2 that pass the sample gate
    // and belong to a different model family than the primary.
    //
    // Design principles:
    //   1. Shadows must be from a different ModelFamily than the primary.
    //   2. Prefer architectures with complementary strengths.
    //   3. Include at least one "safe" choice (BaggedLogistic/GBM) and one
    //      "exploratory" choice (SVGP/DANN/QuantileRf) per rotation.

    private static readonly Dictionary<LearnerArchitecture, LearnerArchitecture[]> ShadowPreference = new()
    {
        // BaggedEnsemble family
        [LearnerArchitecture.BaggedLogistic] = [LearnerArchitecture.Gbm,           LearnerArchitecture.FtTransformer, LearnerArchitecture.Svgp,         LearnerArchitecture.Rocket],
        [LearnerArchitecture.Elm]            = [LearnerArchitecture.Gbm,           LearnerArchitecture.FtTransformer, LearnerArchitecture.Dann,         LearnerArchitecture.Rocket],
        [LearnerArchitecture.Smote]          = [LearnerArchitecture.Gbm,           LearnerArchitecture.FtTransformer, LearnerArchitecture.Svgp,         LearnerArchitecture.Rocket],

        // TreeBoosting family
        [LearnerArchitecture.Gbm]            = [LearnerArchitecture.BaggedLogistic,LearnerArchitecture.Rocket,        LearnerArchitecture.Dann,         LearnerArchitecture.FtTransformer],
        [LearnerArchitecture.AdaBoost]       = [LearnerArchitecture.BaggedLogistic,LearnerArchitecture.FtTransformer, LearnerArchitecture.Svgp,         LearnerArchitecture.Rocket],
        [LearnerArchitecture.QuantileRf]     = [LearnerArchitecture.BaggedLogistic,LearnerArchitecture.FtTransformer, LearnerArchitecture.Dann,         LearnerArchitecture.Rocket],

        // ConvKernel family
        [LearnerArchitecture.Rocket]         = [LearnerArchitecture.Gbm,           LearnerArchitecture.FtTransformer, LearnerArchitecture.Svgp,         LearnerArchitecture.BaggedLogistic],
        [LearnerArchitecture.TemporalConvNet]= [LearnerArchitecture.BaggedLogistic,LearnerArchitecture.FtTransformer, LearnerArchitecture.Gbm,          LearnerArchitecture.Dann],

        // Transformer family
        [LearnerArchitecture.FtTransformer]  = [LearnerArchitecture.Gbm,           LearnerArchitecture.Rocket,        LearnerArchitecture.Svgp,         LearnerArchitecture.BaggedLogistic],
        [LearnerArchitecture.TabNet]         = [LearnerArchitecture.Gbm,           LearnerArchitecture.Rocket,        LearnerArchitecture.Dann,         LearnerArchitecture.BaggedLogistic],

        // GaussianProcess family
        [LearnerArchitecture.Svgp]           = [LearnerArchitecture.Gbm,           LearnerArchitecture.FtTransformer, LearnerArchitecture.Rocket,       LearnerArchitecture.BaggedLogistic],

        // DomainAdaptation family
        [LearnerArchitecture.Dann]           = [LearnerArchitecture.Gbm,           LearnerArchitecture.FtTransformer, LearnerArchitecture.Svgp,         LearnerArchitecture.BaggedLogistic],
    };

    // ── Dependencies ────────────────────────────────────────────────────────

    private readonly IReadApplicationDbContext _db;
    private readonly ILogger<TrainerSelector>  _logger;

    public TrainerSelector(
        IReadApplicationDbContext db,
        ILogger<TrainerSelector>  logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── ITrainerSelector — regime-unaware overload ──────────────────────────

    public Task<LearnerArchitecture> SelectAsync(
        string            symbol,
        Timeframe         timeframe,
        int               sampleCount,
        CancellationToken ct)
        => SelectAsync(symbol, timeframe, sampleCount, regime: null, ct);

    // ── ITrainerSelector — regime-aware primary selection ───────────────────

    public async Task<LearnerArchitecture> SelectAsync(
        string            symbol,
        Timeframe         timeframe,
        int               sampleCount,
        MarketRegimeEnum? regime,
        CancellationToken ct)
    {
        var ctx = _db.GetDbContext();

        // ── 1. Read sample-count thresholds ──────────────────────────────────
        int minSamplesStd  = await GetConfigAsync(ctx, CK_MinSamplesStd,  500,  ct);
        int minSamplesDeep = await GetConfigAsync(ctx, CK_MinSamplesDeep, 2000, ct);

        // ── 2. Try historical best-performer with regime bias ────────────────
        var candidate = await BestHistoricalArchitectureAsync(ctx, symbol, timeframe, regime, ct);

        // ── 3. Fall back to operator-configured default ─────────────────────
        if (candidate is null)
            candidate = await OperatorDefaultAsync(ctx, ct);

        // ── 4. Fall back to regime-specific default ─────────────────────────
        if (candidate is null && regime.HasValue)
            candidate = RegimeDefault.GetValueOrDefault(regime.Value);

        // ── 5. Final fallback ───────────────────────────────────────────────
        var selected = candidate ?? LearnerArchitecture.BaggedLogistic;

        // ── 6. Three-tier sample-count gate ─────────────────────────────────
        selected = ApplySampleGate(selected, sampleCount, minSamplesStd, minSamplesDeep);

        _logger.LogInformation(
            "TrainerSelector: selected {Arch} for {Symbol}/{Tf} ({Samples} samples, regime={Regime})",
            selected, symbol, timeframe, sampleCount, regime?.ToString() ?? "unknown");

        return selected;
    }

    // ── ITrainerSelector — shadow architecture selection ─────────────────────

    public IReadOnlyList<LearnerArchitecture> SelectShadowArchitectures(
        LearnerArchitecture primary,
        int                 sampleCount,
        Timeframe           timeframe)
    {
        const int maxShadows = 2;

        // Determine the primary's model family
        var primaryFamily = ArchitectureFamily.GetValueOrDefault(primary, (ModelFamily)(-1));

        // Get the preferred shadow list for this primary, or fall back to a generic rotation
        var preferences = ShadowPreference.GetValueOrDefault(primary)
                          ?? [LearnerArchitecture.BaggedLogistic, LearnerArchitecture.Gbm, LearnerArchitecture.FtTransformer, LearnerArchitecture.Svgp];

        // Resolve sample-count thresholds (use defaults since this is synchronous)
        const int defaultMinStd  = 500;
        const int defaultMinDeep = 2000;

        var shadows      = new List<LearnerArchitecture>(maxShadows);
        var usedFamilies = new HashSet<ModelFamily>();
        if ((int)primaryFamily >= 0) usedFamilies.Add(primaryFamily);

        foreach (var candidate in preferences)
        {
            if (shadows.Count >= maxShadows) break;
            if (candidate == primary) continue;

            // Enforce model-family diversity
            if (ArchitectureFamily.TryGetValue(candidate, out var family) && !usedFamilies.Add(family))
                continue;

            // Sample-count gate
            if (!PassesSampleGate(candidate, sampleCount, defaultMinStd, defaultMinDeep))
                continue;

            shadows.Add(candidate);
        }

        // If we still need shadows, fill from production architectures not yet used
        if (shadows.Count < maxShadows)
        {
            foreach (var arch in ProductionArchitectures)
            {
                if (shadows.Count >= maxShadows) break;
                if (arch == primary || shadows.Contains(arch)) continue;
                if (ArchitectureFamily.TryGetValue(arch, out var family) && !usedFamilies.Add(family))
                    continue;
                if (!PassesSampleGate(arch, sampleCount, defaultMinStd, defaultMinDeep))
                    continue;
                shadows.Add(arch);
            }
        }

        _logger.LogInformation(
            "TrainerSelector: shadow rotation for primary={Primary} → [{Shadows}]",
            primary, string.Join(", ", shadows));

        return shadows;
    }

    // ── Sample gate ─────────────────────────────────────────────────────────

    private LearnerArchitecture ApplySampleGate(
        LearnerArchitecture arch,
        int                 sampleCount,
        int                 minStd,
        int                 minDeep)
    {
        if (SimpleTier.Contains(arch))
            return arch;

        if (DeepTier.Contains(arch) && sampleCount < minDeep)
        {
            _logger.LogWarning(
                "TrainerSelector: {Samples} samples < deep threshold {Min} — " +
                "downgrading {Arch} → BaggedLogistic",
                sampleCount, minDeep, arch);
            return LearnerArchitecture.BaggedLogistic;
        }

        if (!DeepTier.Contains(arch) && sampleCount < minStd)
        {
            _logger.LogWarning(
                "TrainerSelector: {Samples} samples < standard threshold {Min} — " +
                "downgrading {Arch} → BaggedLogistic",
                sampleCount, minStd, arch);
            return LearnerArchitecture.BaggedLogistic;
        }

        return arch;
    }

    /// <summary>Stateless sample-gate check used by shadow selection (no logging).</summary>
    private static bool PassesSampleGate(LearnerArchitecture arch, int sampleCount, int minStd, int minDeep)
    {
        if (SimpleTier.Contains(arch)) return true;
        if (DeepTier.Contains(arch))   return sampleCount >= minDeep;
        return sampleCount >= minStd;
    }

    // ── Historical selection with regime bias ────────────────────────────────

    /// <summary>
    /// Returns the architecture with the highest recency-weighted composite score
    /// (optionally biased by regime affinity) across recent completed runs for this
    /// symbol/timeframe, or <c>null</c> if no qualifying history exists.
    /// </summary>
    private static async Task<LearnerArchitecture?> BestHistoricalArchitectureAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  symbol,
        Timeframe                               timeframe,
        MarketRegimeEnum?                       regime,
        CancellationToken                       ct)
    {
        var now = DateTime.UtcNow;

        var recentRuns = await ctx.Set<MLTrainingRun>()
            .Where(r => r.Symbol    == symbol    &&
                        r.Timeframe == timeframe  &&
                        r.Status    == RunStatus.Completed &&
                        r.DirectionAccuracy.HasValue)
            .OrderByDescending(r => r.CompletedAt)
            .Take(HistoryWindowRuns)
            .AsNoTracking()
            .Select(r => new
            {
                r.LearnerArchitecture,
                r.DirectionAccuracy,
                r.F1Score,
                r.SharpeRatio,
                r.ExpectedValue,
                r.CompletedAt,
            })
            .ToListAsync(ct);

        if (recentRuns.Count == 0)
            return null;

        // Build the regime affinity lookup for the current regime (if any)
        Dictionary<LearnerArchitecture, double>? affinityMap = null;
        if (regime.HasValue)
            RegimeAffinity.TryGetValue(regime.Value, out affinityMap);

        var bestGroup = recentRuns
            .GroupBy(r => r.LearnerArchitecture)
            .Where(g => g.Count() >= MinRunsPerArchitecture)
            .Select(g =>
            {
                double totalWeight   = 0;
                double weightedScore = 0;

                foreach (var run in g)
                {
                    double w     = RecencyWeight(run.CompletedAt, now);
                    double score = ComputeCompositeScore(
                        run.DirectionAccuracy,
                        run.F1Score,
                        run.SharpeRatio,
                        run.ExpectedValue);

                    weightedScore += w * score;
                    totalWeight   += w;
                }

                double avgScore = totalWeight > 0 ? weightedScore / totalWeight : 0.0;

                // Apply regime affinity bias
                double affinity = RegimeNeutral;
                affinityMap?.TryGetValue(g.Key, out affinity);
                avgScore *= affinity;

                return new
                {
                    Architecture     = g.Key,
                    WeightedAvgScore = avgScore,
                };
            })
            .OrderByDescending(x => x.WeightedAvgScore)
            .FirstOrDefault();

        return bestGroup?.Architecture;
    }

    /// <summary>
    /// Combines up to four evaluation metrics into a single [0, 1] composite score.
    /// Missing metrics are skipped and their weight is redistributed proportionally,
    /// so the score remains comparable across runs with differing metric coverage.
    /// </summary>
    private static double ComputeCompositeScore(
        decimal? directionAccuracy,
        decimal? f1Score,
        decimal? sharpeRatio,
        decimal? expectedValue)
    {
        double totalWeight = 0;
        double score       = 0;

        if (directionAccuracy.HasValue)
        {
            score       += WeightAccuracy * (double)directionAccuracy.Value;
            totalWeight += WeightAccuracy;
        }

        if (f1Score.HasValue)
        {
            score       += WeightF1 * (double)f1Score.Value;
            totalWeight += WeightF1;
        }

        if (sharpeRatio.HasValue)
        {
            score       += WeightSharpe * Sigmoid((double)sharpeRatio.Value / 2.0);
            totalWeight += WeightSharpe;
        }

        if (expectedValue.HasValue)
        {
            score       += WeightEv * Sigmoid((double)expectedValue.Value);
            totalWeight += WeightEv;
        }

        return totalWeight > 0 ? score / totalWeight : 0.5;
    }

    /// <summary>
    /// Exponential recency weight: w = exp(−ln2 × daysSince / halfLife).
    /// </summary>
    private static double RecencyWeight(DateTime? completedAt, DateTime now)
    {
        if (completedAt is null) return 0.5;
        double days = Math.Max(0, (now - completedAt.Value).TotalDays);
        return Math.Exp(-Math.Log(2) * days / RecencyHalfLifeDays);
    }

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

    // ── Operator default ────────────────────────────────────────────────────

    private static async Task<LearnerArchitecture?> OperatorDefaultAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        CancellationToken                       ct)
    {
        var raw = await ctx.Set<EngineConfig>()
            .Where(c => c.Key == CK_DefaultArch && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return Enum.TryParse<LearnerArchitecture>(raw, ignoreCase: true, out var arch)
            ? arch
            : null;
    }

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
        where T : struct
    {
        var raw = await ctx.Set<EngineConfig>()
            .Where(c => c.Key == key && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        try { return (T)Convert.ChangeType(raw, typeof(T)); }
        catch { return defaultValue; }
    }
}
