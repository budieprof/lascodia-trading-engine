using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
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
///   <item><b>Regime-conditional bias:</b> when a non-stale <see cref="MarketRegimeEnum"/>
///         is supplied, each candidate's composite score is multiplied by a blended regime
///         affinity factor that starts from static priors and converges towards empirical
///         per-regime accuracy as history accumulates. A transition cooldown attenuates
///         the affinity strength when the regime was detected very recently.</item>
///   <item><b>Historical performance (UCB1):</b> queries the last
///         <see cref="HistoryWindowRuns"/> completed <see cref="MLTrainingRun"/>s for the
///         symbol/timeframe pair, groups by <see cref="LearnerArchitecture"/>, and picks
///         the one with the highest <b>UCB1 score</b> (recency-weighted composite +
///         exploration bonus inversely proportional to run count). Requires at least
///         <see cref="MinRunsPerArchitecture"/> runs per candidate to be considered.</item>
///   <item><b>Cross-symbol cold start:</b> when no history exists for the target symbol,
///         borrows from instruments sharing the same base or quote currency (looked up
///         via the <see cref="CurrencyPair"/> entity for variable-length symbols).</item>
///   <item><b>Operator default:</b> reads <c>MLTraining:DefaultArchitecture</c> from
///         <see cref="EngineConfig"/>.</item>
///   <item><b>Regime default:</b> a built-in default architecture for the supplied regime
///         when no history or operator config exists.</item>
///   <item><b>Fallback:</b> <see cref="LearnerArchitecture.BaggedLogistic"/>.</item>
/// </list>
/// Each pipeline step returns a <b>ranked list</b> of candidates. A <b>three-tier
/// sample-count gate</b> walks the list and picks the highest-scoring architecture
/// that meets the sample requirement:
/// <list type="bullet">
///   <item><see cref="SimpleTier"/> architectures are always eligible.</item>
///   <item>Standard-tier architectures require ≥ <c>MLTraining:MinSamplesForComplexModel</c>
///         samples (default 500).</item>
///   <item><see cref="DeepTier"/> architectures require ≥ <c>MLTraining:MinSamplesForDeepModel</c>
///         samples (default 2 000).</item>
/// </list>
/// If no ranked candidate passes the gate, the pipeline falls through to the
/// next step. The final fallback is <see cref="LearnerArchitecture.BaggedLogistic"/>
/// (SimpleTier — always passes).
/// </remarks>
[RegisterService]
public sealed class TrainerSelector : ITrainerSelector, IDisposable
{
    private const string CK_DefaultArch        = "MLTraining:DefaultArchitecture";
    private const string CK_MinSamplesStd      = "MLTraining:MinSamplesForComplexModel";
    private const string CK_MinSamplesDeep     = "MLTraining:MinSamplesForDeepModel";
    private const string CK_HistoryMaxDays     = "MLTraining:HistoryMaxAgeDays";
    private const string CK_MinComposite       = "MLTraining:MinCompositeScore";
    private const string CK_RegimeStaleMins    = "MLTraining:RegimeStalenessMinutes";
    private const string CK_RegimeCooldownMins = "MLTraining:RegimeTransitionCooldownMinutes";
    private const string CK_Ucb1Exploration    = "MLTraining:Ucb1ExplorationConstant";
    private const string CK_RegimeWindowHours  = "MLTraining:RegimeWindowHours";
    private const string CK_CacheTtlMinutes    = "MLTraining:CacheTtlMinutes";
    private const string CK_MaxCrossSymbols    = "MLTraining:MaxCrossSymbolCount";
    private const string CK_BlockedArchitectures = "MLTraining:BlockedArchitectures";

    // ── Improvement #7: Configurable temporal decay ──────────────────────
    private const string CK_RecencyHalfLifeDays    = "MLTraining:RecencyHalfLifeDays";
    private const string CK_SteepDecayMultiplier   = "MLTraining:SteepDecayMultiplier";

    // ── Improvement #4: Graduated sample gates ──────────────────────────
    private const string CK_UseGraduatedSampleGate = "MLTraining:UseGraduatedSampleGate";
    private const string CK_SampleGateHardFloor    = "MLTraining:SampleGateHardFloorFraction";

    // ── Improvement #2: Drift-aware selection ───────────────────────────
    private const string CK_DriftAwareBoost        = "MLTraining:DriftAwareBoost";

    // ── Improvement #8: Abstention-aware ranking ────────────────────────
    private const string CK_WeightAbstention       = "MLTraining:WeightAbstention";

    // ── Improvement #12: Shadow regime affinity ─────────────────────────
    private const string CK_ShadowRegimeAffinityWt = "MLTraining:ShadowRegimeAffinityWeight";

    private const int    HistoryWindowRuns         = 30;
    private const int    DefaultHistoryMaxDays     = 90;
    private const double DefaultMinCompositeScore  = 0.35;
    private const int    DefaultRegimeStaleMins    = 120;
    private const int    DefaultRegimeCooldownMins = 30;
    private const int    DefaultRegimeWindowHours  = 24;
    private const int    MinRunsPerArchitecture    = 2;
    private const double RecencyHalfLifeDays       = 30.0;
    private const int    MaxRegimeWindows          = 200;
    private const int    DefaultCacheTtlMinutes    = 5;
    private const int    DefaultMaxCrossSymbols    = 5;

    /// <summary>
    /// If an architecture's most recent run is older than this many days, its UCB1
    /// score is penalised so that stale-but-previously-good architectures don't
    /// dominate purely via the exploration bonus.
    /// </summary>
    private const double ArchStalenessDays         = 60.0;

    /// <summary>
    /// Maximum penalty factor applied to stale architectures. A value of 0.5 means
    /// an architecture whose latest run is infinitely old gets its score halved.
    /// </summary>
    private const double ArchStalenessMaxPenalty    = 0.5;

    /// <summary>Default UCB1 exploration constant — controls exploration vs exploitation trade-off.</summary>
    private const double DefaultUcb1ExplorationConstant = 1.41;

    /// <summary>
    /// Discount factor applied to cross-symbol borrowed scores to reduce their
    /// influence relative to native symbol history.
    /// </summary>
    private const double CrossSymbolDiscountFactor = 0.75;

    /// <summary>
    /// Minimum number of completed runs a cross-symbol candidate must have
    /// before it is considered for borrowing. Prevents a single lucky run
    /// from driving cold-start selection.
    /// </summary>
    private const int MinCrossSymbolRunsPerCandidate = 5;

    /// <summary>
    /// Number of empirical regime-specific runs required before empirical affinity
    /// fully replaces the static prior. Below this count, a linear blend is used.
    /// </summary>
    private const int EmpiricalAffinityMaturityRuns = 20;

    /// <summary>
    /// Sharpe ratios beyond this absolute value are clamped before normalization
    /// to prevent extreme outliers from dominating the composite score.
    /// </summary>
    private const double SharpeClamp = 4.0;

    /// <summary>
    /// Expected-value beyond this absolute value is clamped before normalization.
    /// </summary>
    private const double EvClamp = 3.0;

    /// <summary>
    /// Hardcoded TTL for the config batch cache entry itself. This cannot be
    /// configurable via EngineConfig (chicken-and-egg: we need to read config
    /// to get the TTL, but the config is the thing being cached).
    /// </summary>
    private static readonly TimeSpan ConfigBatchCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Shortened TTL used when caching a null operator default, so that a
    /// newly-added EngineConfig entry is picked up within ~1 minute instead
    /// of waiting the full configurable TTL.
    /// </summary>
    private static readonly TimeSpan NullOperatorDefaultCacheTtl = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Maximum time to wait for a cache-population lock before falling through
    /// to uncached execution. Kept short (5s) to prevent convoy stalls when a
    /// lock holder is slow — most DB queries complete in &lt;100ms.
    /// </summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

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

    private static readonly HashSet<LearnerArchitecture> StandardTier =
    [
        LearnerArchitecture.FtTransformer,
        LearnerArchitecture.TabNet,
        LearnerArchitecture.Svgp,
        LearnerArchitecture.Dann,
    ];

    private static readonly HashSet<LearnerArchitecture> DeepTier =
    [
        LearnerArchitecture.TemporalConvNet,
    ];

    // ── All 12 production-grade (A+) architectures ──────────────────────────

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

    // ── Static regime-conditional affinity matrix (prior) ──────────────────

    private static readonly Dictionary<MarketRegimeEnum, Dictionary<LearnerArchitecture, double>> RegimeAffinityPrior = new()
    {
        [MarketRegimeEnum.Trending] = new()
        {
            [LearnerArchitecture.Gbm]             = RegimeBoost,
            [LearnerArchitecture.TemporalConvNet]  = RegimeBoost,
            [LearnerArchitecture.AdaBoost]         = RegimeBoost,
            [LearnerArchitecture.Dann]             = RegimeBoost,
            [LearnerArchitecture.FtTransformer]    = RegimeNeutral,
            [LearnerArchitecture.TabNet]           = RegimeNeutral,
            [LearnerArchitecture.BaggedLogistic]   = RegimeNeutral,
            [LearnerArchitecture.Rocket]           = RegimeNeutral,
            [LearnerArchitecture.QuantileRf]       = RegimeNeutral,
            [LearnerArchitecture.Smote]            = RegimePenalty,
            [LearnerArchitecture.Elm]              = RegimePenalty,
            [LearnerArchitecture.Svgp]             = RegimePenalty,
        },
        [MarketRegimeEnum.Ranging] = new()
        {
            [LearnerArchitecture.BaggedLogistic]   = RegimeBoost,
            [LearnerArchitecture.Elm]              = RegimeBoost,
            [LearnerArchitecture.Rocket]           = RegimeBoost,
            [LearnerArchitecture.Svgp]             = RegimeBoost,
            [LearnerArchitecture.Smote]            = RegimeNeutral,
            [LearnerArchitecture.QuantileRf]       = RegimeNeutral,
            [LearnerArchitecture.FtTransformer]    = RegimeNeutral,
            [LearnerArchitecture.TabNet]           = RegimeNeutral,
            [LearnerArchitecture.Dann]             = RegimeNeutral,
            [LearnerArchitecture.AdaBoost]         = RegimePenalty,
            [LearnerArchitecture.Gbm]              = RegimePenalty,
            [LearnerArchitecture.TemporalConvNet]  = RegimePenalty,
        },
        [MarketRegimeEnum.HighVolatility] = new()
        {
            [LearnerArchitecture.FtTransformer]    = RegimeBoost,
            [LearnerArchitecture.TabNet]           = RegimeBoost,
            [LearnerArchitecture.BaggedLogistic]   = RegimeBoost,
            [LearnerArchitecture.Dann]             = RegimeBoost,
            [LearnerArchitecture.Smote]            = RegimeBoost,
            [LearnerArchitecture.QuantileRf]       = RegimeBoost,
            [LearnerArchitecture.Gbm]              = RegimeNeutral,
            [LearnerArchitecture.TemporalConvNet]  = RegimeNeutral,
            [LearnerArchitecture.AdaBoost]         = RegimeNeutral,
            [LearnerArchitecture.Svgp]             = RegimeNeutral,
            [LearnerArchitecture.Elm]              = RegimePenalty,
            [LearnerArchitecture.Rocket]           = RegimePenalty,
        },
        [MarketRegimeEnum.LowVolatility] = new()
        {
            [LearnerArchitecture.Elm]              = RegimeBoost,
            [LearnerArchitecture.Rocket]           = RegimeBoost,
            [LearnerArchitecture.Svgp]             = RegimeBoost,
            [LearnerArchitecture.QuantileRf]       = RegimeBoost,
            [LearnerArchitecture.BaggedLogistic]   = RegimeNeutral,
            [LearnerArchitecture.Smote]            = RegimeNeutral,
            [LearnerArchitecture.Dann]             = RegimeNeutral,
            [LearnerArchitecture.AdaBoost]         = RegimePenalty,
            [LearnerArchitecture.Gbm]              = RegimePenalty,
            [LearnerArchitecture.FtTransformer]    = RegimePenalty,
            [LearnerArchitecture.TabNet]           = RegimePenalty,
            [LearnerArchitecture.TemporalConvNet]  = RegimePenalty,
        },
        [MarketRegimeEnum.Crisis] = new()
        {
            [LearnerArchitecture.BaggedLogistic]   = RegimeBoost,
            [LearnerArchitecture.Dann]             = RegimeBoost,
            [LearnerArchitecture.QuantileRf]       = RegimeBoost,
            [LearnerArchitecture.Smote]            = RegimeBoost,
            [LearnerArchitecture.FtTransformer]    = RegimeNeutral,
            [LearnerArchitecture.TabNet]           = RegimeNeutral,
            [LearnerArchitecture.Svgp]             = RegimeNeutral,
            [LearnerArchitecture.Gbm]              = RegimeNeutral,
            [LearnerArchitecture.AdaBoost]         = RegimePenalty,
            [LearnerArchitecture.Elm]              = RegimePenalty,
            [LearnerArchitecture.Rocket]           = RegimePenalty,
            [LearnerArchitecture.TemporalConvNet]  = RegimePenalty,
        },
        [MarketRegimeEnum.Breakout] = new()
        {
            [LearnerArchitecture.Gbm]             = RegimeBoost,
            [LearnerArchitecture.TemporalConvNet]  = RegimeBoost,
            [LearnerArchitecture.Rocket]           = RegimeBoost,
            [LearnerArchitecture.AdaBoost]         = RegimeBoost,
            [LearnerArchitecture.FtTransformer]    = RegimeNeutral,
            [LearnerArchitecture.TabNet]           = RegimeNeutral,
            [LearnerArchitecture.Dann]             = RegimeNeutral,
            [LearnerArchitecture.BaggedLogistic]   = RegimeNeutral,
            [LearnerArchitecture.QuantileRf]       = RegimeNeutral,
            [LearnerArchitecture.Smote]            = RegimePenalty,
            [LearnerArchitecture.Elm]              = RegimePenalty,
            [LearnerArchitecture.Svgp]             = RegimePenalty,
        },
    };

    // ── Regime default architectures ────────────────────────────────────────

    private static readonly Dictionary<MarketRegimeEnum, LearnerArchitecture> RegimeDefault = new()
    {
        [MarketRegimeEnum.Trending]       = LearnerArchitecture.Gbm,
        [MarketRegimeEnum.Ranging]        = LearnerArchitecture.BaggedLogistic,
        [MarketRegimeEnum.HighVolatility] = LearnerArchitecture.FtTransformer,
        [MarketRegimeEnum.LowVolatility]  = LearnerArchitecture.Elm,
        [MarketRegimeEnum.Crisis]         = LearnerArchitecture.BaggedLogistic,
        [MarketRegimeEnum.Breakout]       = LearnerArchitecture.Gbm,
    };

    // ── Static validation ─────────────────────────────────────────────────

    static TrainerSelector()
    {
        var missingFamily = ProductionArchitectures.Where(a => !ArchitectureFamily.ContainsKey(a)).ToList();
        if (missingFamily.Count > 0)
            throw new InvalidOperationException(
                $"TrainerSelector: ProductionArchitectures contains architectures missing from ArchitectureFamily: {string.Join(", ", missingFamily)}");

        var missingTier = ProductionArchitectures
            .Where(a => !SimpleTier.Contains(a) && !StandardTier.Contains(a) && !DeepTier.Contains(a))
            .ToList();
        if (missingTier.Count > 0)
            throw new InvalidOperationException(
                $"TrainerSelector: ProductionArchitectures contains architectures not classified in any tier: {string.Join(", ", missingTier)}");

        var allRegimes = Enum.GetValues<MarketRegimeEnum>();

        var missingAffinity = allRegimes.Where(r => !RegimeAffinityPrior.ContainsKey(r)).ToList();
        if (missingAffinity.Count > 0)
            throw new InvalidOperationException(
                $"TrainerSelector: RegimeAffinityPrior is missing entries for regimes: {string.Join(", ", missingAffinity)}");

        var missingDefault = allRegimes.Where(r => !RegimeDefault.ContainsKey(r)).ToList();
        if (missingDefault.Count > 0)
            throw new InvalidOperationException(
                $"TrainerSelector: RegimeDefault is missing entries for regimes: {string.Join(", ", missingDefault)}");

        // Ensure every regime's affinity map covers all production architectures
        foreach (var regime in allRegimes)
        {
            var missingArchs = ProductionArchitectures
                .Where(a => !RegimeAffinityPrior[regime].ContainsKey(a))
                .ToList();
            if (missingArchs.Count > 0)
                throw new InvalidOperationException(
                    $"TrainerSelector: RegimeAffinityPrior[{regime}] is missing entries for architectures: {string.Join(", ", missingArchs)}");
        }
    }

    // ── Dependencies ────────────────────────────────────────────────────────

    private readonly IReadApplicationDbContext _db;
    private readonly IMemoryCache             _cache;
    private readonly ILogger<TrainerSelector>  _logger;
    private readonly TradingMetrics            _metrics;
    private readonly TimeProvider              _timeProvider;
    private readonly SemaphoreSlim             _configCacheLock      = new(1, 1);
    private readonly SemaphoreSlim             _affinityCacheLock    = new(1, 1);
    private readonly SemaphoreSlim             _operatorDefaultLock  = new(1, 1);
    private readonly SemaphoreSlim             _recentRunsCacheLock  = new(1, 1);

    public TrainerSelector(
        IReadApplicationDbContext db,
        IMemoryCache             cache,
        ILogger<TrainerSelector>  logger,
        TradingMetrics            metrics,
        TimeProvider              timeProvider)
    {
        _db           = db;
        _cache        = cache;
        _logger       = logger;
        _metrics      = metrics;
        _timeProvider = timeProvider;
    }

    // ── ITrainerSelector — regime-unaware overload ──────────────────────────

    public Task<LearnerArchitecture> SelectAsync(
        string            symbol,
        Timeframe         timeframe,
        int               sampleCount,
        CancellationToken ct)
        => SelectAsync(symbol, timeframe, sampleCount, regime: null, regimeDetectedAt: null, ct);

    // ── ITrainerSelector — regime-aware primary selection ───────────────────

    public async Task<LearnerArchitecture> SelectAsync(
        string            symbol,
        Timeframe         timeframe,
        int               sampleCount,
        MarketRegimeEnum? regime,
        DateTime?         regimeDetectedAt,
        CancellationToken ct)
    {
        var ctx = _db.GetDbContext();

        // ── 1. Read config thresholds (single batch query) ──────────────────
        var cfg = await LoadConfigBatchAsync(ctx, ct).ConfigureAwait(false);
        int    minSamplesStd      = cfg.GetInt(CK_MinSamplesStd,       500);
        int    minSamplesDeep     = cfg.GetInt(CK_MinSamplesDeep,      2000);
        int    historyMaxDays     = cfg.GetInt(CK_HistoryMaxDays,      DefaultHistoryMaxDays);
        double minComposite       = cfg.GetDouble(CK_MinComposite,     DefaultMinCompositeScore);
        int    regimeStaleMins    = cfg.GetInt(CK_RegimeStaleMins,     DefaultRegimeStaleMins);
        int    regimeCooldownMins = cfg.GetInt(CK_RegimeCooldownMins,  DefaultRegimeCooldownMins);
        double ucb1Exploration    = cfg.GetDouble(CK_Ucb1Exploration,  DefaultUcb1ExplorationConstant);
        int    regimeWindowHours  = cfg.GetInt(CK_RegimeWindowHours,   DefaultRegimeWindowHours);
        var    cacheTtl           = TimeSpan.FromMinutes(cfg.GetInt(CK_CacheTtlMinutes, DefaultCacheTtlMinutes));
        int    maxCrossSymbols    = cfg.GetInt(CK_MaxCrossSymbols,     DefaultMaxCrossSymbols);

        // Improvement #7: configurable decay
        double cfgRecencyHalfLife = cfg.GetDouble(CK_RecencyHalfLifeDays, RecencyHalfLifeDays);
        double cfgSteepMultiplier = cfg.GetDouble(CK_SteepDecayMultiplier, 1.0);

        // Improvement #4: graduated sample gate (accepts "true", "1", or any non-zero int)
        var graduatedRaw = cfg.GetString(CK_UseGraduatedSampleGate, "0");
        bool useGraduatedGate = graduatedRaw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                graduatedRaw == "1";
        double sampleGateFloor    = cfg.GetDouble(CK_SampleGateHardFloor, 0.20);

        // Improvement #8: abstention weight
        double weightAbstention   = cfg.GetDouble(CK_WeightAbstention, 0.0);

        // Improvement #12: shadow regime affinity weight
        double shadowAffinityWt   = cfg.GetDouble(CK_ShadowRegimeAffinityWt, 0.30);

        // ── 1b. Parse blocked architectures from config ───────────────────
        var blockedArchitectures = new HashSet<LearnerArchitecture>();
        var blockedStr = cfg.GetString(CK_BlockedArchitectures, "");
        if (!string.IsNullOrWhiteSpace(blockedStr))
        {
            foreach (var token in blockedStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<LearnerArchitecture>(token, ignoreCase: true, out var blocked))
                    blockedArchitectures.Add(blocked);
            }
        }

        // ── 2. Staleness gate — ignore stale regime data ────────────────────
        var effectiveRegime = ApplyRegimeStalenessGate(regime, regimeDetectedAt, regimeStaleMins, timeframe);

        // ── 2b. Regime transition cooldown — attenuate affinity for very recent transitions
        double regimeConfidence = ComputeRegimeConfidence(effectiveRegime, regimeDetectedAt, regimeCooldownMins, timeframe);

        // ── 2c. Pre-warm independent caches ───────────────────────────────
        // LoadRecentRunsAsync and BuildRawAffinityMapAsync read from the scoped
        // IReadApplicationDbContext, so they must run sequentially — EF Core
        // does not permit concurrent operations on a single DbContext instance
        // ("A second operation was started on this context instance before a
        // previous operation completed"). The subsequent calls inside
        // RankedHistoricalArchitecturesAsync still benefit because both helpers
        // populate IMemoryCache, so only the first run pays the DB cost.
        await LoadRecentRunsAsync(ctx, symbol, timeframe, historyMaxDays, cacheTtl, ct).ConfigureAwait(false);
        if (effectiveRegime.HasValue)
        {
            await BuildRawAffinityMapAsync(
                ctx, symbol, timeframe, effectiveRegime.Value, historyMaxDays, regimeWindowHours, cacheTtl, ct, shadowAffinityWt)
                .ConfigureAwait(false);
        }

        // ── 3. Try historical best-performer with UCB1 + blended regime affinity
        //       Returns a ranked list so the sample gate can walk it instead of
        //       always downgrading the winner to BaggedLogistic.
        var rankedCandidates = await RankedHistoricalArchitecturesAsync(
            ctx, symbol, timeframe, effectiveRegime, regimeConfidence,
            historyMaxDays, regimeWindowHours, minComposite, ucb1Exploration, cacheTtl,
            cfgRecencyHalfLife, cfgSteepMultiplier, weightAbstention, shadowAffinityWt, ct)
            .ConfigureAwait(false);

        // Filter out blocked architectures from candidates
        var filteredCandidates = blockedArchitectures.Count > 0
            ? rankedCandidates.Where(c => !blockedArchitectures.Contains(c.Arch)).ToList()
            : rankedCandidates;

        // Improvement #4: graduated sample gate — apply continuous discount instead of hard pass/fail
        var candidate = useGraduatedGate
            ? PickBestWithGraduatedGate(filteredCandidates, sampleCount, minSamplesStd, minSamplesDeep, timeframe, sampleGateFloor)
            : PickBestPassingSampleGate(filteredCandidates, sampleCount, minSamplesStd, minSamplesDeep, timeframe);
        int fallbackDepth = 1; // step 3 (historical)

        // ── 4. Cold-start: borrow from correlated symbols ─────────────────
        if (candidate is null)
        {
            fallbackDepth = 2;
            var crossRanked = await CrossSymbolRankedFallbackAsync(
                ctx, symbol, timeframe, effectiveRegime, regimeConfidence,
                historyMaxDays, regimeWindowHours, minComposite, ucb1Exploration,
                maxCrossSymbols, cacheTtl,
                cfgRecencyHalfLife, cfgSteepMultiplier, weightAbstention, shadowAffinityWt, ct)
                .ConfigureAwait(false);

            var filteredCross = blockedArchitectures.Count > 0
                ? crossRanked.Where(c => !blockedArchitectures.Contains(c.Arch)).ToList()
                : crossRanked;
            candidate = useGraduatedGate
                ? PickBestWithGraduatedGate(filteredCross, sampleCount, minSamplesStd, minSamplesDeep, timeframe, sampleGateFloor)
                : PickBestPassingSampleGate(filteredCross, sampleCount, minSamplesStd, minSamplesDeep, timeframe);
        }

        // ── 5. Fall back to operator-configured default ─────────────────────
        if (candidate is null)
        {
            fallbackDepth = 3;
            var opDefault = await OperatorDefaultAsync(ctx, cacheTtl, ct).ConfigureAwait(false);
            if (opDefault.HasValue
                && !blockedArchitectures.Contains(opDefault.Value)
                && PassesSampleGate(opDefault.Value, sampleCount, minSamplesStd, minSamplesDeep, timeframe))
                candidate = opDefault.Value;
        }

        // ── 6. Fall back to regime-specific default ─────────────────────────
        if (candidate is null && effectiveRegime.HasValue)
        {
            fallbackDepth = 4;
            var regDefault = RegimeDefault.GetValueOrDefault(effectiveRegime.Value);
            if (!blockedArchitectures.Contains(regDefault)
                && PassesSampleGate(regDefault, sampleCount, minSamplesStd, minSamplesDeep, timeframe))
                candidate = regDefault;
        }

        // ── 7. Final fallback (BaggedLogistic is SimpleTier — always passes) ─
        if (candidate is null)
            fallbackDepth = 5;

        var selected = candidate ?? LearnerArchitecture.BaggedLogistic;

        _logger.LogInformation(
            "TrainerSelector: selected {Arch} for {Symbol}/{Tf} ({Samples} samples, regime={Regime}, confidence={Confidence:F2}, candidatesEvaluated={CandidateCount}, fallbackDepth={FallbackDepth})",
            selected, symbol, timeframe, sampleCount, effectiveRegime?.ToString() ?? "unknown", regimeConfidence, rankedCandidates.Count, fallbackDepth);

        RecordSelectionMetric(selected, effectiveRegime, "primary");
        RecordFallbackDepthMetric(symbol, timeframe, fallbackDepth);

        return selected;
    }

    // ── ITrainerSelector — shadow architecture selection ─────────────────────

    public async Task<IReadOnlyList<LearnerArchitecture>> SelectShadowArchitecturesAsync(
        LearnerArchitecture primary,
        string              symbol,
        Timeframe           timeframe,
        int                 sampleCount,
        MarketRegimeEnum?   regime,
        DateTime?           regimeDetectedAt,
        CancellationToken   ct)
    {
        const int maxShadows = 2;

        var ctx = _db.GetDbContext();

        var cfg = await LoadConfigBatchAsync(ctx, ct).ConfigureAwait(false);
        int minSamplesStd      = cfg.GetInt(CK_MinSamplesStd,       500);
        int minSamplesDeep     = cfg.GetInt(CK_MinSamplesDeep,      2000);
        int historyMaxDays     = cfg.GetInt(CK_HistoryMaxDays,      DefaultHistoryMaxDays);
        int regimeStaleMins    = cfg.GetInt(CK_RegimeStaleMins,     DefaultRegimeStaleMins);
        int regimeCooldownMins = cfg.GetInt(CK_RegimeCooldownMins,  DefaultRegimeCooldownMins);
        int regimeWindowHours  = cfg.GetInt(CK_RegimeWindowHours,   DefaultRegimeWindowHours);
        var cacheTtl           = TimeSpan.FromMinutes(cfg.GetInt(CK_CacheTtlMinutes, DefaultCacheTtlMinutes));
        double shadowAffinityWt = cfg.GetDouble(CK_ShadowRegimeAffinityWt, 0.30);

        var effectiveRegime  = ApplyRegimeStalenessGate(regime, regimeDetectedAt, regimeStaleMins, timeframe);
        double regimeConfidence = ComputeRegimeConfidence(effectiveRegime, regimeDetectedAt, regimeCooldownMins, timeframe);

        // Build regime affinity once — reused for both ranking and regime-penalty filtering
        var affinityMap = effectiveRegime.HasValue
            ? ApplyConfidence(
                await BuildRawAffinityMapAsync(ctx, symbol, timeframe, effectiveRegime.Value, historyMaxDays, regimeWindowHours, cacheTtl, ct, shadowAffinityWt)
                    .ConfigureAwait(false),
                regimeConfidence)
            : null;

        // Rank all production architectures by historical performance for this symbol/tf
        var rankedArchitectures = await RankArchitecturesByHistoryAsync(
            ctx, symbol, timeframe, historyMaxDays, cacheTtl, affinityMap, ct)
            .ConfigureAwait(false);

        // Build a combined candidate list: ranked history first, then remaining
        // production architectures (with score 0) so the fallback fill is unified.
        var allCandidates = new List<(LearnerArchitecture Arch, double Score)>(rankedArchitectures);
        foreach (var arch in ProductionArchitectures)
        {
            if (!allCandidates.Exists(c => c.Arch == arch))
                allCandidates.Add((arch, 0.0));
        }

        // Soft regime ranking: multiply each candidate's score by its regime affinity
        // (already applied in RankArchitecturesByHistoryAsync for ranked ones, but
        // for the zero-score fallback candidates, use affinity as the tiebreaker).
        // Sort by: (affinityBucket descending, score descending) where bucket is:
        //   2 = boosted (>= RegimeBoost threshold), 1 = neutral, 0 = penalised.
        // This replaces the hard cutoff — penalised candidates are deprioritised
        // but still selectable if no better family-diverse option exists.
        if (affinityMap is not null)
        {
            allCandidates.Sort((a, b) =>
            {
                int bucketA = AffinityBucket(affinityMap, a.Arch);
                int bucketB = AffinityBucket(affinityMap, b.Arch);
                int cmp = bucketB.CompareTo(bucketA);
                return cmp != 0 ? cmp : b.Score.CompareTo(a.Score);
            });
        }

        var primaryFamily = ArchitectureFamily.GetValueOrDefault(primary, (ModelFamily)(-1));

        var shadows      = new List<LearnerArchitecture>(maxShadows);
        var usedFamilies = new HashSet<ModelFamily>();
        if ((int)primaryFamily >= 0) usedFamilies.Add(primaryFamily);

        // Walk sorted list — pick best candidates from different families
        foreach (var (arch, _) in allCandidates)
        {
            if (shadows.Count >= maxShadows) break;
            if (arch == primary) continue;

            if (ArchitectureFamily.TryGetValue(arch, out var family) && !usedFamilies.Add(family))
                continue;

            if (!PassesSampleGate(arch, sampleCount, minSamplesStd, minSamplesDeep, timeframe))
                continue;

            shadows.Add(arch);
        }

        _logger.LogInformation(
            "TrainerSelector: shadow rotation for primary={Primary}, regime={Regime} → [{Shadows}]",
            primary, effectiveRegime?.ToString() ?? "unknown", string.Join(", ", shadows));

        foreach (var s in shadows)
            RecordSelectionMetric(s, effectiveRegime, "shadow");

        return shadows;
    }

    // ── Regime staleness gate ────────────────────────────────────────────────

    /// <summary>
    /// Timeframe-relative multipliers for the regime staleness threshold.
    /// The configured threshold is the baseline for H1. Faster timeframes
    /// use a shorter window (regime changes matter sooner); slower timeframes
    /// use a longer one (a 2-hour-old regime is still very recent on D1).
    /// </summary>
    private static readonly Dictionary<Timeframe, double> TimeframeStalenessScale = new()
    {
        [Timeframe.M1]  = 0.10,  // 120 min × 0.10 = 12 min
        [Timeframe.M5]  = 0.25,  // 120 min × 0.25 = 30 min
        [Timeframe.M15] = 0.50,  // 120 min × 0.50 = 60 min
        [Timeframe.H1]  = 1.00,  // baseline
        [Timeframe.H4]  = 3.00,  // 120 min × 3.00 = 360 min (6h)
        [Timeframe.D1]  = 12.00, // 120 min × 12.0 = 1440 min (24h)
    };

    private MarketRegimeEnum? ApplyRegimeStalenessGate(
        MarketRegimeEnum? regime,
        DateTime?         regimeDetectedAt,
        int               baseStalenessMinutes,
        Timeframe         timeframe)
    {
        if (!regime.HasValue)
            return null;

        if (!regimeDetectedAt.HasValue)
            return regime; // no timestamp — trust caller

        double scale = TimeframeStalenessScale.GetValueOrDefault(timeframe, 1.0);
        double scaledThreshold = baseStalenessMinutes * scale;

        var age = _timeProvider.GetUtcNow() - regimeDetectedAt.Value;
        if (age.TotalMinutes > scaledThreshold)
        {
            _logger.LogWarning(
                "TrainerSelector: regime {Regime} is stale ({AgeMinutes:F0} min > {Threshold:F0} min threshold for {Timeframe}) — ignoring",
                regime.Value, age.TotalMinutes, scaledThreshold, timeframe);
            return null;
        }

        return regime;
    }

    // ── Regime transition cooldown ────────────────────────────────────────────

    /// <summary>
    /// Returns a confidence factor in [0, 1] that attenuates regime affinity strength
    /// when the regime was detected very recently (within the cooldown window).
    /// A regime detected exactly at <c>UtcNow</c> yields a low confidence;
    /// one detected ≥ the scaled cooldown ago yields full confidence (1.0).
    /// The cooldown is scaled by <paramref name="timeframe"/> using the same
    /// multipliers as the staleness gate so both gates stay proportional.
    /// </summary>
    private double ComputeRegimeConfidence(
        MarketRegimeEnum? effectiveRegime,
        DateTime?         regimeDetectedAt,
        int               baseCooldownMinutes,
        Timeframe         timeframe)
    {
        if (!effectiveRegime.HasValue || !regimeDetectedAt.HasValue || baseCooldownMinutes <= 0)
            return 1.0;

        double scale = TimeframeStalenessScale.GetValueOrDefault(timeframe, 1.0);
        double scaledCooldown = baseCooldownMinutes * scale;

        double ageMinutes = (_timeProvider.GetUtcNow() - regimeDetectedAt.Value).TotalMinutes;
        if (ageMinutes >= scaledCooldown)
            return 1.0;

        // Linear ramp from 0.3 → 1.0 over the cooldown window
        // (0.3 floor prevents completely ignoring a valid regime signal)
        const double floor = 0.3;
        return floor + (1.0 - floor) * (ageMinutes / scaledCooldown);
    }

    // ── Sample gate ─────────────────────────────────────────────────────────

    /// <summary>
    /// Timeframe-relative multipliers for the sample-count gate. Faster
    /// timeframes produce noisier data, so complex models need more samples
    /// to generalise. Slower timeframes have richer per-bar information,
    /// so the threshold can be relaxed.
    /// </summary>
    private static readonly Dictionary<Timeframe, double> TimeframeSampleScale = new()
    {
        [Timeframe.M1]  = 2.00,  // very noisy — double the sample requirement
        [Timeframe.M5]  = 1.50,
        [Timeframe.M15] = 1.25,
        [Timeframe.H1]  = 1.00,  // baseline
        [Timeframe.H4]  = 0.75,
        [Timeframe.D1]  = 0.50,  // daily bars are information-dense
    };

    private static bool PassesSampleGate(LearnerArchitecture arch, int sampleCount, int minStd, int minDeep, Timeframe timeframe)
    {
        double scale = TimeframeSampleScale.GetValueOrDefault(timeframe, 1.0);
        if (SimpleTier.Contains(arch))   return true;
        if (DeepTier.Contains(arch))     return sampleCount >= (int)(minDeep * scale);
        if (StandardTier.Contains(arch)) return sampleCount >= (int)(minStd * scale);
        // Unclassified architecture — reject to be safe
        return false;
    }

    // ── Historical ranked selection with UCB1 + blended regime affinity ─────

    private async Task<List<(LearnerArchitecture Arch, double Score)>> RankedHistoricalArchitecturesAsync(
        DbContext         ctx,
        string            symbol,
        Timeframe         timeframe,
        MarketRegimeEnum? regime,
        double            regimeConfidence,
        int               historyMaxDays,
        int               regimeWindowHours,
        double            minCompositeScore,
        double            ucb1Exploration,
        TimeSpan          cacheTtl,
        double            cfgRecencyHalfLife,
        double            cfgSteepMultiplier,
        double            weightAbstention,
        double            shadowAffinityWt,
        CancellationToken ct)
    {
        var runs = await LoadRecentRunsAsync(ctx, symbol, timeframe, historyMaxDays, cacheTtl, ct).ConfigureAwait(false);
        if (runs.Count == 0)
            return [];

        return await RankByUcb1Async(
            ctx, symbol, timeframe, runs, regime, regimeConfidence,
            historyMaxDays, regimeWindowHours, minCompositeScore, ucb1Exploration, cacheTtl,
            cfgRecencyHalfLife, cfgSteepMultiplier, weightAbstention, shadowAffinityWt, ct).ConfigureAwait(false);
    }

    // ── Cold-start: borrow from correlated symbols ──────────────────────────

    private async Task<List<(LearnerArchitecture Arch, double Score)>> CrossSymbolRankedFallbackAsync(
        DbContext         ctx,
        string            symbol,
        Timeframe         timeframe,
        MarketRegimeEnum? regime,
        double            regimeConfidence,
        int               historyMaxDays,
        int               regimeWindowHours,
        double            minCompositeScore,
        double            ucb1Exploration,
        int               maxCrossSymbols,
        TimeSpan          cacheTtl,
        double            cfgRecencyHalfLife,
        double            cfgSteepMultiplier,
        double            weightAbstention,
        double            shadowAffinityWt,
        CancellationToken ct)
    {
        // Look up base/quote currencies from the CurrencyPair entity
        // instead of hardcoding 3-char extraction — handles variable-length symbols
        // (e.g. BTCUSD, XAUUSD, USDMXN, etc.)
        var currencyPair = await ctx.Set<CurrencyPair>()
            .Where(cp => cp.Symbol == symbol && !cp.IsDeleted)
            .Select(cp => new { cp.BaseCurrency, cp.QuoteCurrency })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        string? baseCcy;
        string? quoteCcy;

        if (currencyPair is not null)
        {
            baseCcy  = currencyPair.BaseCurrency;
            quoteCcy = currencyPair.QuoteCurrency;
        }
        else if (symbol.Length >= 6)
        {
            // Graceful fallback for symbols not yet in the CurrencyPair table
            baseCcy  = symbol[..3];
            quoteCcy = symbol[3..6];

            _logger.LogWarning(
                "TrainerSelector: symbol {Symbol} not found in CurrencyPair table — " +
                "falling back to 3-char substring parsing (base={Base}, quote={Quote}). " +
                "Add this symbol to the CurrencyPair table for reliable cross-symbol borrowing",
                symbol, baseCcy, quoteCcy);
        }
        else
        {
            return [];
        }

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-historyMaxDays);

        // Find symbols sharing base or quote currency via the CurrencyPair table
        // instead of error-prone StartsWith/EndsWith on raw symbol strings.
        var relatedSymbolsFromPairs = await ctx.Set<CurrencyPair>()
            .Where(cp => !cp.IsDeleted &&
                         cp.Symbol != symbol &&
                         (cp.BaseCurrency == baseCcy || cp.QuoteCurrency == quoteCcy))
            .Select(cp => cp.Symbol)
            .ToListAsync(ct).ConfigureAwait(false);

        // If the CurrencyPair table has related instruments, restrict to those with runs;
        // otherwise fall back to string matching for symbols not yet in the table.
        List<string> relatedSymbols;
        if (relatedSymbolsFromPairs.Count > 0)
        {
            relatedSymbols = await ctx.Set<MLTrainingRun>()
                .Where(r => relatedSymbolsFromPairs.Contains(r.Symbol) &&
                            r.Timeframe == timeframe &&
                            r.Status    == RunStatus.Completed &&
                            r.DirectionAccuracy.HasValue &&
                            r.CompletedAt.HasValue &&
                            r.CompletedAt.Value >= cutoff)
                .GroupBy(r => r.Symbol)
                .Where(g => g.Count() >= MinCrossSymbolRunsPerCandidate)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .Take(maxCrossSymbols)
                .ToListAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // Fallback: string matching for symbols missing from CurrencyPair table.
            // Length guard (>= 6) prevents short symbols like index tickers from
            // matching spuriously on 3-char currency code prefixes/suffixes.
            relatedSymbols = await ctx.Set<MLTrainingRun>()
                .Where(r => r.Timeframe == timeframe &&
                            r.Status    == RunStatus.Completed &&
                            r.DirectionAccuracy.HasValue &&
                            r.CompletedAt.HasValue &&
                            r.CompletedAt.Value >= cutoff &&
                            r.Symbol != symbol &&
                            r.Symbol.Length >= 6 &&
                            (r.Symbol.StartsWith(baseCcy) || r.Symbol.EndsWith(quoteCcy)))
                .GroupBy(r => r.Symbol)
                .Where(g => g.Count() >= MinCrossSymbolRunsPerCandidate)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .Take(maxCrossSymbols)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        if (relatedSymbols.Count == 0)
            return [];

        // ── Compute per-symbol correlation weights from candle returns ────
        var correlationWeights = await ComputeCrossSymbolCorrelationsAsync(
            ctx, symbol, relatedSymbols, timeframe, historyMaxDays, cacheTtl, ct).ConfigureAwait(false);

        // Load up to HistoryWindowRuns per related symbol so that no single
        // symbol dominates the borrowed dataset. A single query fetches all
        // candidates; per-symbol capping is applied in-memory to avoid N+1
        // roundtrips (maxCrossSymbols ≤ 5, HistoryWindowRuns = 30 → ≤150 rows kept).
        var allCrossRuns = await ctx.Set<MLTrainingRun>()
            .Where(r => relatedSymbols.Contains(r.Symbol) &&
                        r.Timeframe == timeframe &&
                        r.Status    == RunStatus.Completed &&
                        r.DirectionAccuracy.HasValue &&
                        r.CompletedAt.HasValue &&
                        r.CompletedAt.Value >= cutoff)
            .OrderByDescending(r => r.CompletedAt)
            .AsNoTracking()
            .Select(r => new ArchRunProjection
            {
                LearnerArchitecture = r.LearnerArchitecture,
                DirectionAccuracy   = r.DirectionAccuracy,
                F1Score             = r.F1Score,
                SharpeRatio         = r.SharpeRatio,
                ExpectedValue       = r.ExpectedValue,
                CompletedAt         = r.CompletedAt,
                Symbol              = r.Symbol,
                AbstentionPrecision = r.AbstentionPrecision,
                DriftTriggerType    = r.DriftTriggerType,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var runs = allCrossRuns
            .GroupBy(r => r.Symbol)
            .SelectMany(g => g.Take(HistoryWindowRuns))
            .ToList();

        if (runs.Count == 0)
            return [];

        // Compute the effective discount as a weighted average of per-symbol
        // correlation-based discounts. Symbols with higher correlation to the
        // target get a higher weight (closer to 1.0), others are discounted more.
        double effectiveDiscount = ComputeWeightedDiscount(runs, correlationWeights);

        _logger.LogInformation(
            "TrainerSelector: cold-start for {Symbol}/{Tf} — borrowing from {RelatedSymbols} (effectiveDiscount={Discount:F3})",
            symbol, timeframe, string.Join(", ", relatedSymbols), effectiveDiscount);

        return await RankByUcb1Async(
            ctx, symbol, timeframe, runs, regime, regimeConfidence,
            historyMaxDays, regimeWindowHours, minCompositeScore, ucb1Exploration, cacheTtl,
            cfgRecencyHalfLife, cfgSteepMultiplier, weightAbstention, shadowAffinityWt, ct,
            scoreDiscount: effectiveDiscount).ConfigureAwait(false);
    }

    // ── Shared UCB1 ranking logic ───────────────────────────────────────────

    /// <summary>
    /// Returns all candidate architectures ranked by UCB1 score (descending),
    /// filtered by the minimum composite threshold. Callers walk the list to
    /// find the highest-scoring architecture that passes the sample gate.
    /// </summary>
    private async Task<List<(LearnerArchitecture Arch, double Score)>> RankByUcb1Async(
        DbContext                  ctx,
        string                     symbol,
        Timeframe                  timeframe,
        List<ArchRunProjection>    runs,
        MarketRegimeEnum?          regime,
        double                     regimeConfidence,
        int                        historyMaxDays,
        int                        regimeWindowHours,
        double                     minCompositeScore,
        double                     ucb1Exploration,
        TimeSpan                   cacheTtl,
        double                     cfgRecencyHalfLife,
        double                     cfgSteepMultiplier,
        double                     weightAbstention,
        double                     shadowAffinityWt,
        CancellationToken          ct,
        double                     scoreDiscount = 1.0)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Improvement #12: Build raw regime affinity map with shadow data blended in
        Dictionary<LearnerArchitecture, double>? affinityMap = null;
        if (regime.HasValue)
        {
            var rawMap = await BuildRawAffinityMapAsync(
                ctx, symbol, timeframe, regime.Value, historyMaxDays, regimeWindowHours,
                cacheTtl, ct, shadowAffinityWt).ConfigureAwait(false);
            affinityMap = ApplyConfidence(rawMap, regimeConfidence);
        }

        // Separate architectures with sufficient runs (UCB1-eligible) from
        // under-explored ones. Under-explored architectures are appended at
        // the end with their raw (discounted) score — no exploration bonus —
        // so they can still be selected if all UCB1 candidates fail the
        // sample gate, but they don't get an artificial boost.
        var groups = runs
            .GroupBy(r => r.LearnerArchitecture)
            .Select(g =>
            {
                // Improvement #7: pass configurable decay params
                // Improvement #8: pass abstention weight
                double avgScore = ScoreArchitectureGroup(
                    g, now, affinityMap, scoreDiscount,
                    cfgRecencyHalfLife, cfgSteepMultiplier, weightAbstention);
                int    runCount = g.Count();
                return (Architecture: g.Key, AvgScore: avgScore, RunCount: runCount);
            })
            .ToList();

        // Improvement #2: drift-aware boost — if we know the most recent drift trigger
        // for this symbol/timeframe, boost architectures that recovered well from similar drift
        var driftBoosts = await ComputeDriftAwareBoostsAsync(ctx, symbol, timeframe, historyMaxDays, cacheTtl, ct)
            .ConfigureAwait(false);

        int totalEligibleRuns = groups.Where(g => g.RunCount >= MinRunsPerArchitecture).Sum(g => g.RunCount);
        // Fallback: if no architecture meets the minimum, use total runs for UCB1 denominator
        if (totalEligibleRuns == 0)
            totalEligibleRuns = runs.Count;
        // Guard against log(0) in the UCB1 formula when no runs exist at all
        if (totalEligibleRuns <= 0)
            totalEligibleRuns = 1;

        var ranked = new List<(LearnerArchitecture Arch, double Score)>();
        var underExplored = new List<(LearnerArchitecture Arch, double Score)>();

        foreach (var (arch, avgScore, runCount) in groups)
        {
            // Improvement #2: apply drift-aware boost
            double boostedScore = avgScore;
            if (driftBoosts.TryGetValue(arch, out double driftBoost))
                boostedScore *= driftBoost;

            if (runCount < MinRunsPerArchitecture)
            {
                if (boostedScore >= minCompositeScore)
                    underExplored.Add((arch, boostedScore));
                continue;
            }

            // UCB1 exploration bonus: sqrt(ln(N) / n_i)
            double ucb1Bonus = ucb1Exploration * Math.Sqrt(Math.Log(totalEligibleRuns) / runCount);
            double ucb1Score = boostedScore + ucb1Bonus;

            if (boostedScore >= minCompositeScore)
                ranked.Add((arch, ucb1Score));
        }

        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        underExplored.Sort((a, b) => b.Score.CompareTo(a.Score));
        ranked.AddRange(underExplored);

        return ranked;
    }

    /// <summary>
    /// Walks a ranked list of architectures and returns the first one that
    /// passes the three-tier sample-count gate, or null if none qualifies.
    /// Logs a warning when higher-ranked candidates are skipped.
    /// </summary>
    private LearnerArchitecture? PickBestPassingSampleGate(
        List<(LearnerArchitecture Arch, double Score)> ranked,
        int sampleCount,
        int minSamplesStd,
        int minSamplesDeep,
        Timeframe timeframe)
    {
        LearnerArchitecture? topSkipped = null;

        foreach (var (arch, score) in ranked)
        {
            if (PassesSampleGate(arch, sampleCount, minSamplesStd, minSamplesDeep, timeframe))
            {
                if (topSkipped.HasValue)
                {
                    _logger.LogWarning(
                        "TrainerSelector: top-ranked {Skipped} (score={SkippedScore:F4}) failed sample gate " +
                        "({Samples} samples) — selected next-best {Selected} instead",
                        topSkipped.Value, ranked[0].Score, sampleCount, arch);
                }

                return arch;
            }

            topSkipped ??= arch;
        }

        if (topSkipped.HasValue)
        {
            _logger.LogWarning(
                "TrainerSelector: all {Count} ranked candidates failed sample gate ({Samples} samples)",
                ranked.Count, sampleCount);
        }

        return null;
    }

    // ── Rank all architectures by history (for shadow selection) ────────────

    /// <summary>
    /// Ranks architectures by weighted-average composite score (no UCB1 bonus).
    /// Accepts a pre-built <paramref name="affinityMap"/> so callers that already
    /// have one don't redundantly rebuild it.
    /// </summary>
    private async Task<List<(LearnerArchitecture Arch, double Score)>> RankArchitecturesByHistoryAsync(
        DbContext                                    ctx,
        string                                       symbol,
        Timeframe                                    timeframe,
        int                                          historyMaxDays,
        TimeSpan                                     cacheTtl,
        Dictionary<LearnerArchitecture, double>?     affinityMap,
        CancellationToken                            ct)
    {
        var now    = _timeProvider.GetUtcNow().UtcDateTime;
        var runs   = await LoadRecentRunsAsync(ctx, symbol, timeframe, historyMaxDays, cacheTtl, ct).ConfigureAwait(false);

        // Intentionally scores by weighted average only (no UCB1 exploration bonus).
        // Shadows should be the best *known* alternatives, not exploratory picks —
        // the primary selection already handles exploration.
        var ranked = runs
            .GroupBy(r => r.LearnerArchitecture)
            .Select(g => (Arch: g.Key, Score: ScoreArchitectureGroup(g, now, affinityMap)))
            .OrderByDescending(x => x.Score)
            .ToList();

        return ranked;
    }

    // ── Blended regime affinity (static prior + empirical) — raw (no confidence) ─

    /// <summary>
    /// Builds a raw (confidence-unattenuated) affinity map blending static priors with
    /// empirical per-regime accuracy for the given symbol and timeframe. The result is
    /// cached by symbol/timeframe/regime/historyMaxDays. Callers must apply
    /// <see cref="ApplyConfidence"/> afterwards using the current regime confidence,
    /// which varies with time and should not be baked into the cache.
    /// </summary>
    private async Task<Dictionary<LearnerArchitecture, double>> BuildRawAffinityMapAsync(
        DbContext         ctx,
        string            symbol,
        Timeframe         timeframe,
        MarketRegimeEnum  regime,
        int               historyMaxDays,
        int               regimeWindowHours,
        TimeSpan          cacheTtl,
        CancellationToken ct,
        double            shadowAffinityWt = 0.30)
    {
        // Note: shadowAffinityWt is intentionally NOT in the cache key. The cached map
        // stores the blended result for the *maximum* shadow weight (1.0 equivalent).
        // The caller-supplied weight is applied post-cache via a separate blend step,
        // avoiding cache fragmentation and ensuring InvalidateCache evicts all entries.
        var cacheKey = $"TrainerSelector:RawAffinity:{symbol}:{timeframe}:{regime}:{historyMaxDays}:{regimeWindowHours}";
        if (_cache.TryGetValue<Dictionary<LearnerArchitecture, double>>(cacheKey, out var cached))
            return cached!;

        bool lockAcquired = await _affinityCacheLock.WaitAsync(LockTimeout, ct).ConfigureAwait(false);
        if (!lockAcquired)
            _logger.LogWarning("TrainerSelector: affinity cache lock timed out — proceeding uncached");
        try
        {
            // Double-check after acquiring lock
            if (lockAcquired && _cache.TryGetValue<Dictionary<LearnerArchitecture, double>>(cacheKey, out cached))
                return cached!;

            // Load the static prior
            RegimeAffinityPrior.TryGetValue(regime, out var staticMap);

            // Compute empirical affinity: average DirectionAccuracy per architecture
            // for runs that were completed during this regime
            var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-historyMaxDays);

            // Find regime windows bounded by actual transitions instead of a
            // fixed duration. Query all snapshots (any regime) within the
            // history window so we can compute the end of each target-regime
            // period as the start of the next different-regime snapshot.
            // Query all snapshots (any regime) so we can bound each target-regime
            // period by the next different-regime snapshot. A generous safety cap
            // (5000) prevents runaway memory use if regime detection runs very
            // frequently, while being large enough to never exclude target-regime
            // entries in practice. The output (regime ranges) is further capped
            // to MaxRegimeWindows below.
            const int maxSnapshotRows = 5000;
            var allSnapshots = await ctx.Set<MarketRegimeSnapshot>()
                .Where(s => s.DetectedAt >= cutoff)
                .OrderBy(s => s.DetectedAt)
                .Select(s => new { s.Regime, s.DetectedAt })
                .Take(maxSnapshotRows)
                .ToListAsync(ct).ConfigureAwait(false);

            // Build (Start, End) ranges for the target regime, bounded by the
            // next snapshot's DetectedAt or the regimeWindowHours cap (whichever
            // comes first). This prevents attributing runs to a regime after
            // the market has already transitioned to a different one.
            // Take the most recent MaxRegimeWindows ranges to bound memory usage.
            var regimeRanges = new List<(DateTime Start, DateTime End)>();
            var maxWindow = TimeSpan.FromHours(regimeWindowHours);

            for (int i = 0; i < allSnapshots.Count; i++)
            {
                if (allSnapshots[i].Regime != regime)
                    continue;

                var start = allSnapshots[i].DetectedAt;
                // End is the earlier of: next snapshot's DetectedAt, or start + maxWindow
                var naturalEnd = start.Add(maxWindow);
                var nextTransition = i + 1 < allSnapshots.Count
                    ? allSnapshots[i + 1].DetectedAt
                    : naturalEnd;

                regimeRanges.Add((start, naturalEnd < nextTransition ? naturalEnd : nextTransition));
            }

            // Keep only the most recent windows to bound downstream work
            if (regimeRanges.Count > MaxRegimeWindows)
                regimeRanges = regimeRanges.GetRange(regimeRanges.Count - MaxRegimeWindows, MaxRegimeWindows);

            Dictionary<LearnerArchitecture, (double sumAcc, int count)>? empirical = null;

            if (regimeRanges.Count > 0)
            {
                // Merge overlapping/adjacent ranges to prevent double-counting
                var mergedRanges = MergeRanges(regimeRanges);

                var earliestStart = mergedRanges[0].Start;
                var latestEnd     = mergedRanges[^1].End;

                // Single query: fetch runs for this symbol/timeframe within the overall date envelope
                var allCandidateRuns = await ctx.Set<MLTrainingRun>()
                    .Where(r => r.Symbol == symbol &&
                                r.Timeframe == timeframe &&
                                r.Status == RunStatus.Completed &&
                                r.DirectionAccuracy.HasValue &&
                                r.CompletedAt.HasValue &&
                                r.CompletedAt.Value >= earliestStart &&
                                r.CompletedAt.Value <= latestEnd)
                    .AsNoTracking()
                    .Select(r => new
                    {
                        r.LearnerArchitecture,
                        r.DirectionAccuracy,
                        CompletedAt = r.CompletedAt!.Value,
                    })
                    .ToListAsync(ct).ConfigureAwait(false);

                // Filter in-memory against merged (non-overlapping) ranges using binary search
                empirical = new Dictionary<LearnerArchitecture, (double sumAcc, int count)>();

                foreach (var run in allCandidateRuns)
                {
                    if (!FallsWithinMergedRange(mergedRanges, run.CompletedAt))
                        continue;

                    if (!empirical.TryGetValue(run.LearnerArchitecture, out var acc))
                        acc = (0, 0);

                    empirical[run.LearnerArchitecture] = (
                        acc.sumAcc + (double)run.DirectionAccuracy!.Value,
                        acc.count + 1);
                }
            }

            // ── Improvement #12: Load shadow evaluation regime breakdowns ────
            // Shadow outcomes are the strongest signal for per-regime architecture
            // performance — they measured live data, not just training hold-out metrics.
            Dictionary<LearnerArchitecture, (double sumAcc, int count)>? shadowEmpirical = null;

            if (shadowAffinityWt > 0)
            {
                var shadowBreakdowns = await ctx.Set<MLShadowRegimeBreakdown>()
                    .Where(b => b.Regime == regime &&
                                b.TotalPredictions >= 10 &&
                                !b.IsDeleted &&
                                b.ShadowEvaluation.Symbol == symbol &&
                                b.ShadowEvaluation.Timeframe == timeframe &&
                                b.ShadowEvaluation.CompletedAt.HasValue &&
                                b.ShadowEvaluation.CompletedAt.Value >= cutoff)
                    .Select(b => new
                    {
                        b.ShadowEvaluation.ChallengerModel.LearnerArchitecture,
                        b.ChallengerAccuracy,
                        b.TotalPredictions,
                    })
                    .AsNoTracking()
                    .ToListAsync(ct).ConfigureAwait(false);

                if (shadowBreakdowns.Count > 0)
                {
                    shadowEmpirical = new Dictionary<LearnerArchitecture, (double, int)>();
                    foreach (var sb in shadowBreakdowns)
                    {
                        if (!shadowEmpirical.TryGetValue(sb.LearnerArchitecture, out var acc))
                            acc = (0, 0);
                        shadowEmpirical[sb.LearnerArchitecture] = (
                            acc.Item1 + (double)sb.ChallengerAccuracy,
                            acc.Item2 + 1);
                    }
                }
            }

            // Blend: result = alpha * empirical_affinity + (1 - alpha) * static_prior
            // where alpha = min(1, empirical_run_count / EmpiricalAffinityMaturityRuns)
            // Then further blend with shadow empirical data when available.
            //
            // Note: regime confidence is NOT applied here — it is applied by the caller
            // via ApplyConfidence() so the cached map stays valid across confidence changes.
            var blended = new Dictionary<LearnerArchitecture, double>();

            foreach (var arch in ProductionArchitectures)
            {
                double staticAffinity = RegimeNeutral;
                if (staticMap is not null && staticMap.TryGetValue(arch, out var sa))
                    staticAffinity = sa;

                double empiricalAffinity = RegimeNeutral;
                double alpha = 0;

                if (empirical is not null && empirical.TryGetValue(arch, out var emp) && emp.count > 0)
                {
                    double avgAcc = emp.sumAcc / emp.count;
                    empiricalAffinity = AccuracyToAffinity(avgAcc);
                    alpha = Math.Min(1.0, (double)emp.count / EmpiricalAffinityMaturityRuns);
                }

                double baseBlend = alpha * empiricalAffinity + (1 - alpha) * staticAffinity;

                // Improvement #12: blend shadow data on top of the base blend
                if (shadowEmpirical is not null &&
                    shadowEmpirical.TryGetValue(arch, out var shadowEmp) &&
                    shadowEmp.count > 0)
                {
                    double shadowAvgAcc = shadowEmp.sumAcc / shadowEmp.count;
                    double shadowAffinity = AccuracyToAffinity(shadowAvgAcc);
                    double shadowAlpha = Math.Min(1.0, (double)shadowEmp.count / EmpiricalAffinityMaturityRuns);
                    double effectiveShadowWeight = shadowAffinityWt * shadowAlpha;

                    // Weighted blend: shadow data partially overrides training-run data
                    baseBlend = (1.0 - effectiveShadowWeight) * baseBlend +
                                effectiveShadowWeight * shadowAffinity;
                }

                blended[arch] = baseBlend;
            }

            _cache.Set(cacheKey, blended, cacheTtl);
            return blended;
        }
        finally
        {
            if (lockAcquired) _affinityCacheLock.Release();
        }
    }

    // ── Accuracy-to-affinity conversion (shared by training-run and shadow paths)

    /// <summary>
    /// Converts accuracy (0–1) to an affinity multiplier via continuous piecewise-linear
    /// mapping centred on 0.50 (coin flip).
    /// Below 0.50: ramp from <see cref="RegimePenalty"/> (at ≤0.40) to <see cref="RegimeNeutral"/> (at 0.50).
    /// Above 0.50: ramp from <see cref="RegimeNeutral"/> (at 0.50) to <see cref="RegimeBoost"/> (at ≥0.60).
    /// </summary>
    private static double AccuracyToAffinity(double avgAcc) => avgAcc switch
    {
        >= 0.60 => RegimeBoost,
        >= 0.50 => RegimeNeutral + (avgAcc - 0.50) * (RegimeBoost - RegimeNeutral) / 0.10,
        >= 0.40 => RegimePenalty + (avgAcc - 0.40) * (RegimeNeutral - RegimePenalty) / 0.10,
        _       => RegimePenalty,
    };

    // ── Apply regime confidence attenuation to a raw affinity map ────────────

    /// <summary>
    /// Returns a new affinity map with each value attenuated towards <see cref="RegimeNeutral"/>
    /// by the given <paramref name="confidence"/> factor. This is applied after cache lookup
    /// so that the cached raw map stays valid across varying confidence levels.
    /// </summary>
    private static Dictionary<LearnerArchitecture, double> ApplyConfidence(
        Dictionary<LearnerArchitecture, double> rawMap,
        double                                  confidence)
    {
        if (confidence >= 1.0)
            return rawMap;

        var result = new Dictionary<LearnerArchitecture, double>(rawMap.Count);
        foreach (var (arch, raw) in rawMap)
            result[arch] = RegimeNeutral + confidence * (raw - RegimeNeutral);

        return result;
    }

    // ── Merge overlapping/adjacent time ranges ──────────────────────────────

    /// <summary>
    /// Given a list of (Start, End) ranges, produces a sorted list of
    /// non-overlapping merged ranges. Prevents double-counting runs that
    /// fall within multiple overlapping or adjacent regime windows.
    /// </summary>
    private static List<(DateTime Start, DateTime End)> MergeRanges(
        List<(DateTime Start, DateTime End)> ranges)
    {
        if (ranges.Count == 0)
            return [];

        var sorted = ranges.OrderBy(r => r.Start).ToList();
        var merged = new List<(DateTime Start, DateTime End)> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            var current = sorted[i];
            var last    = merged[^1];

            if (current.Start <= last.End)
            {
                // Overlapping or adjacent — extend the existing range
                merged[^1] = (last.Start, current.End > last.End ? current.End : last.End);
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    /// <summary>
    /// Binary search on sorted, non-overlapping merged ranges to check whether
    /// <paramref name="timestamp"/> falls within any range. O(log n) vs O(n) linear scan.
    /// </summary>
    private static bool FallsWithinMergedRange(
        List<(DateTime Start, DateTime End)> mergedRanges,
        DateTime                             timestamp)
    {
        int lo = 0, hi = mergedRanges.Count - 1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            var range = mergedRanges[mid];

            if (timestamp < range.Start)
                hi = mid - 1;
            else if (timestamp > range.End)
                lo = mid + 1;
            else
                return true; // timestamp is within [Start, End]
        }

        return false;
    }

    // ── DB helpers ───────────────────────────────────────────────────────────

    private async Task<List<ArchRunProjection>> LoadRecentRunsAsync(
        DbContext         ctx,
        string            symbol,
        Timeframe         timeframe,
        int               historyMaxDays,
        TimeSpan          cacheTtl,
        CancellationToken ct)
    {
        var cacheKey = $"TrainerSelector:RecentRuns:{symbol}:{timeframe}:{historyMaxDays}";
        if (_cache.TryGetValue<List<ArchRunProjection>>(cacheKey, out var cached))
            return cached!;

        bool lockAcquired = await _recentRunsCacheLock.WaitAsync(LockTimeout, ct).ConfigureAwait(false);
        if (!lockAcquired)
            _logger.LogWarning("TrainerSelector: recent-runs cache lock timed out — proceeding uncached");
        try
        {
            // Double-check after acquiring lock
            if (lockAcquired && _cache.TryGetValue<List<ArchRunProjection>>(cacheKey, out cached))
                return cached!;

            var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-historyMaxDays);

            var runs = await ctx.Set<MLTrainingRun>()
                .Where(r => r.Symbol    == symbol    &&
                            r.Timeframe == timeframe  &&
                            r.Status    == RunStatus.Completed &&
                            r.DirectionAccuracy.HasValue &&
                            r.CompletedAt.HasValue &&
                            r.CompletedAt.Value >= cutoff)
                .OrderByDescending(r => r.CompletedAt)
                .Take(HistoryWindowRuns)
                .AsNoTracking()
                .Select(r => new ArchRunProjection
                {
                    LearnerArchitecture = r.LearnerArchitecture,
                    DirectionAccuracy   = r.DirectionAccuracy,
                    F1Score             = r.F1Score,
                    SharpeRatio         = r.SharpeRatio,
                    ExpectedValue       = r.ExpectedValue,
                    CompletedAt         = r.CompletedAt,
                    Symbol              = r.Symbol,
                    AbstentionPrecision = r.AbstentionPrecision,
                    DriftTriggerType    = r.DriftTriggerType,
                })
                .ToListAsync(ct).ConfigureAwait(false);

            _cache.Set(cacheKey, runs, cacheTtl);
            return runs;
        }
        finally
        {
            if (lockAcquired) _recentRunsCacheLock.Release();
        }
    }

    // ── Shared architecture group scoring ──────────────────────────────────

    /// <summary>
    /// Minimum number of runs required before trend detection kicks in.
    /// With fewer runs, a slope estimate is too noisy to be useful.
    /// </summary>
    private const int MinRunsForTrend = 4;

    /// <summary>
    /// Maximum penalty applied when an architecture has a strongly declining trend.
    /// A value of 0.7 means the score can be reduced by up to 30%.
    /// </summary>
    private const double TrendPenaltyFloor = 0.70;

    /// <summary>
    /// Computes a recency-weighted, affinity-adjusted, staleness-penalised,
    /// trend-aware composite score for a group of training runs sharing the
    /// same architecture.
    /// Used by both UCB1 selection and shadow ranking to keep scoring consistent.
    /// </summary>
    private double ScoreArchitectureGroup(
        IGrouping<LearnerArchitecture, ArchRunProjection> group,
        DateTime                                          now,
        Dictionary<LearnerArchitecture, double>?          affinityMap,
        double                                            scoreDiscount = 1.0,
        double                                            cfgRecencyHalfLife = RecencyHalfLifeDays,
        double                                            cfgSteepMultiplier = 1.0,
        double                                            weightAbstention = 0.0)
    {
        double   totalWeight   = 0;
        double   weightedScore = 0;
        DateTime mostRecentRun = DateTime.MinValue;

        // Collect per-run scores ordered by time for trend detection
        var runScores = new List<(DateTime CompletedAt, double Score)>();

        foreach (var run in group)
        {
            // Improvement #7: use configurable half-life for recency weighting
            double w     = RecencyWeight(run.CompletedAt, now, cfgRecencyHalfLife);

            // Improvement #8: include abstention precision in composite score
            double score = ComputeCompositeScore(
                run.DirectionAccuracy,
                run.F1Score,
                run.SharpeRatio,
                run.ExpectedValue,
                run.AbstentionPrecision,
                weightAbstention);

            weightedScore += w * score;
            totalWeight   += w;

            var completed = run.CompletedAt ?? DateTime.MinValue;
            if (completed > mostRecentRun)
                mostRecentRun = completed;

            if (run.CompletedAt.HasValue)
                runScores.Add((completed, score));
        }

        double avgScore = totalWeight > 0 ? weightedScore / totalWeight : 0.0;

        // Apply cross-symbol discount when using borrowed data
        avgScore *= scoreDiscount;

        // Apply blended regime affinity (with confidence already applied)
        if (affinityMap is not null && affinityMap.TryGetValue(group.Key, out var affinity))
            avgScore *= affinity;

        // Improvement #7: steeper two-phase decay — after ArchStalenessDays, apply
        // the configurable steep multiplier to accelerate the decay rate.
        double daysSinceLastRun = (now - mostRecentRun).TotalDays;
        if (daysSinceLastRun > ArchStalenessDays)
        {
            double excessDays = daysSinceLastRun - ArchStalenessDays;
            // Phase 2 uses steepened half-life: halfLife / steepMultiplier
            double effectiveHalfLife = cfgRecencyHalfLife / Math.Max(cfgSteepMultiplier, 1.0);
            double decayFactor = ArchStalenessMaxPenalty +
                (1.0 - ArchStalenessMaxPenalty) * Math.Exp(-Math.Log(2) * excessDays / effectiveHalfLife);
            avgScore *= decayFactor;
        }

        // Trend detection: if enough runs exist, compute a simple OLS slope
        // of composite scores over time. A negative slope means the architecture
        // is degrading — apply a penalty proportional to the decline rate.
        if (runScores.Count >= MinRunsForTrend)
        {
            double trendPenalty = ComputeTrendPenalty(runScores, group.Key);
            avgScore *= trendPenalty;
        }

        return avgScore;
    }

    /// <summary>
    /// Computes a penalty factor in [<see cref="TrendPenaltyFloor"/>, 1.0] based on the
    /// OLS slope of composite scores over time. A flat or rising trend returns 1.0
    /// (no penalty). A declining trend returns a value below 1.0, clamped at the floor.
    /// The slope is normalised by the mean score so architectures at different absolute
    /// levels are treated comparably.
    /// </summary>
    private double ComputeTrendPenalty(List<(DateTime CompletedAt, double Score)> runScores, LearnerArchitecture arch)
    {
        runScores.Sort((a, b) => a.CompletedAt.CompareTo(b.CompletedAt));

        // Use run index as x-axis (0, 1, 2, ...) — simpler than days-since-first
        // and avoids issues with uneven spacing dominating the slope estimate.
        int n = runScores.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++)
        {
            double x = i;
            double y = runScores[i].Score;
            sumX  += x;
            sumY  += y;
            sumXY += x * y;
            sumXX += x * x;
        }

        double denom = n * sumXX - sumX * sumX;
        if (denom == 0) return 1.0;

        double slope = (n * sumXY - sumX * sumY) / denom;
        double meanY = sumY / n;

        // Non-negative slope → no penalty
        if (slope >= 0 || meanY <= 0)
            return 1.0;

        // Normalised slope: decline per run as a fraction of mean score
        double normSlope = slope / meanY; // negative

        // Map normalised slope to penalty: -0.10/run → floor, 0 → 1.0
        // Linear interpolation between 1.0 (no decline) and TrendPenaltyFloor (steep decline)
        const double slopeThreshold = -0.10; // normalised slope at which max penalty applies
        double t = Math.Clamp(normSlope / slopeThreshold, 0.0, 1.0);
        double penalty = 1.0 - t * (1.0 - TrendPenaltyFloor);

        if (penalty < 1.0)
        {
            _metrics.MLSelectorTrendPenalty.Add(1,
                new KeyValuePair<string, object?>("architecture", arch.ToString()),
                new KeyValuePair<string, object?>("penalty", penalty.ToString("F3")));
        }

        return penalty;
    }

    // ── Composite score ─────────────────────────────────────────────────────

    private static double ComputeCompositeScore(
        decimal? directionAccuracy,
        decimal? f1Score,
        decimal? sharpeRatio,
        decimal? expectedValue,
        decimal? abstentionPrecision = null,
        double   weightAbstention = 0.0)
    {
        double totalWeight = 0;
        double score       = 0;

        if (directionAccuracy.HasValue)
        {
            double clamped = Math.Clamp((double)directionAccuracy.Value, 0.0, 1.0);
            score       += WeightAccuracy * clamped;
            totalWeight += WeightAccuracy;
        }

        if (f1Score.HasValue)
        {
            double clamped = Math.Clamp((double)f1Score.Value, 0.0, 1.0);
            score       += WeightF1 * clamped;
            totalWeight += WeightF1;
        }

        if (sharpeRatio.HasValue)
        {
            double clamped   = Math.Clamp((double)sharpeRatio.Value, -SharpeClamp, SharpeClamp);
            double normalized = (clamped + SharpeClamp) / (2.0 * SharpeClamp);
            score       += WeightSharpe * normalized;
            totalWeight += WeightSharpe;
        }

        if (expectedValue.HasValue)
        {
            double clamped   = Math.Clamp((double)expectedValue.Value, -EvClamp, EvClamp);
            double normalized = (clamped + EvClamp) / (2.0 * EvClamp);
            score       += WeightEv * normalized;
            totalWeight += WeightEv;
        }

        // Improvement #8: abstention-aware ranking — reward architectures that correctly
        // identify when NOT to trade. weightAbstention defaults to 0.0 (disabled).
        if (weightAbstention > 0 && abstentionPrecision.HasValue)
        {
            double clamped = Math.Clamp((double)abstentionPrecision.Value, 0.0, 1.0);
            score       += weightAbstention * clamped;
            totalWeight += weightAbstention;
        }

        return totalWeight > 0 ? score / totalWeight : 0.0;
    }

    // Improvement #7: configurable half-life parameter
    private static double RecencyWeight(DateTime? completedAt, DateTime now, double halfLifeDays = RecencyHalfLifeDays)
    {
        if (completedAt is null) return 0.5;
        double days = Math.Max(0, (now - completedAt.Value).TotalDays);
        return Math.Exp(-Math.Log(2) * days / halfLifeDays);
    }

    // ── Operator default ────────────────────────────────────────────────────

    private async Task<LearnerArchitecture?> OperatorDefaultAsync(
        DbContext         ctx,
        TimeSpan          cacheTtl,
        CancellationToken ct)
    {
        var cacheKey = $"TrainerSelector:Config:{CK_DefaultArch}";

        if (_cache.TryGetValue<LearnerArchitecture?>(cacheKey, out var cached))
            return cached;

        bool lockAcquired = await _operatorDefaultLock.WaitAsync(LockTimeout, ct).ConfigureAwait(false);
        if (!lockAcquired)
            _logger.LogWarning("TrainerSelector: operator-default cache lock timed out — proceeding uncached");
        try
        {
            // Double-check after acquiring lock
            if (lockAcquired && _cache.TryGetValue<LearnerArchitecture?>(cacheKey, out cached))
                return cached;

            var raw = await ctx.Set<EngineConfig>()
                .Where(c => c.Key == CK_DefaultArch && !c.IsDeleted)
                .Select(c => c.Value)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            LearnerArchitecture? result = null;
            if (!string.IsNullOrWhiteSpace(raw) &&
                Enum.TryParse<LearnerArchitecture>(raw, ignoreCase: true, out var arch))
            {
                result = arch;
            }

            // Use shorter TTL for null results so newly-added config is picked up faster.
            // Note: IMemoryCache.TryGetValue returns true for cached null, so the
            // double-check pattern above correctly short-circuits on cached nulls.
            var effectiveTtl = result.HasValue ? cacheTtl : NullOperatorDefaultCacheTtl;
            _cache.Set(cacheKey, result, effectiveTtl);
            return result;
        }
        finally
        {
            if (lockAcquired) _operatorDefaultLock.Release();
        }
    }

    // ── Batch config loader ────────────────────────────────────────────────

    private static readonly string[] AllConfigKeys =
    [
        CK_MinSamplesStd, CK_MinSamplesDeep, CK_HistoryMaxDays,
        CK_MinComposite, CK_RegimeStaleMins, CK_RegimeCooldownMins,
        CK_Ucb1Exploration, CK_RegimeWindowHours,
        CK_CacheTtlMinutes, CK_MaxCrossSymbols,
        CK_BlockedArchitectures,
        CK_RecencyHalfLifeDays, CK_SteepDecayMultiplier,
        CK_UseGraduatedSampleGate, CK_SampleGateHardFloor,
        CK_DriftAwareBoost, CK_WeightAbstention,
        CK_ShadowRegimeAffinityWt,
    ];

    private const string ConfigBatchCacheKey = "TrainerSelector:ConfigBatch";

    private async Task<ConfigBatch> LoadConfigBatchAsync(DbContext ctx, CancellationToken ct)
    {
        if (_cache.TryGetValue<ConfigBatch>(ConfigBatchCacheKey, out var cached))
            return cached!;

        bool lockAcquired = await _configCacheLock.WaitAsync(LockTimeout, ct).ConfigureAwait(false);
        if (!lockAcquired)
            _logger.LogWarning("TrainerSelector: config cache lock timed out — proceeding uncached");
        try
        {
            // Double-check after acquiring lock
            if (lockAcquired && _cache.TryGetValue<ConfigBatch>(ConfigBatchCacheKey, out cached))
                return cached!;

            var rows = await ctx.Set<EngineConfig>()
                .Where(c => AllConfigKeys.Contains(c.Key) && !c.IsDeleted)
                .Select(c => new { c.Key, c.Value })
                .ToListAsync(ct).ConfigureAwait(false);

            var dict = new Dictionary<string, string>(rows.Count);
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.Value))
                    dict[row.Key] = row.Value;
            }

            var batch = new ConfigBatch(dict, _logger);
            _cache.Set(ConfigBatchCacheKey, batch, ConfigBatchCacheTtl);
            return batch;
        }
        finally
        {
            if (lockAcquired) _configCacheLock.Release();
        }
    }

    private sealed class ConfigBatch
    {
        private readonly Dictionary<string, string>   _values;
        private readonly ILogger<TrainerSelector>      _logger;

        public ConfigBatch(Dictionary<string, string> values, ILogger<TrainerSelector> logger)
        {
            _values = values;
            _logger = logger;
        }

        public int GetInt(string key, int defaultValue)
            => TryParse<int>(key, defaultValue);

        public double GetDouble(string key, double defaultValue)
            => TryParse<double>(key, defaultValue);

        public string GetString(string key, string defaultValue)
            => _values.TryGetValue(key, out var raw) ? raw : defaultValue;

        private T TryParse<T>(string key, T defaultValue) where T : struct
        {
            if (!_values.TryGetValue(key, out var raw))
                return defaultValue;

            try { return (T)Convert.ChangeType(raw, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "TrainerSelector: failed to parse EngineConfig '{Key}' value '{Raw}' as {Type} — using default {Default}",
                    key, raw, typeof(T).Name, defaultValue);
                return defaultValue;
            }
        }
    }

    // ── Regime affinity bucket (for shadow soft ranking) ────────────────────

    /// <summary>
    /// Classifies an architecture's regime affinity into a bucket for sorting:
    /// 2 = boosted, 1 = neutral, 0 = penalised. Used instead of a hard cutoff
    /// so penalised architectures are deprioritised but still selectable.
    /// </summary>
    private static int AffinityBucket(Dictionary<LearnerArchitecture, double> affinityMap, LearnerArchitecture arch)
    {
        if (!affinityMap.TryGetValue(arch, out var aff))
            return 1; // unknown → neutral

        if (aff >= RegimeBoost - 0.01)  return 2; // boosted
        if (aff >= RegimeNeutral)       return 1; // neutral
        return 0;                                  // penalised
    }

    // ── Cross-symbol correlation computation ────────────────────────────────

    /// <summary>
    /// Minimum number of overlapping candle returns required to compute a
    /// meaningful Pearson correlation. Below this, the estimate is too noisy.
    /// </summary>
    private const int MinCandlesForCorrelation = 30;

    /// <summary>
    /// Computes Pearson return correlations between <paramref name="targetSymbol"/> and
    /// each of <paramref name="relatedSymbols"/> using daily close-to-close log returns
    /// from the <see cref="Candle"/> table. Results are cached to avoid repeated queries.
    /// Returns |correlation| (absolute value) so negative correlations (inverse pairs)
    /// are also treated as informative for borrowing.
    /// </summary>
    private async Task<Dictionary<string, double>> ComputeCrossSymbolCorrelationsAsync(
        DbContext         ctx,
        string            targetSymbol,
        List<string>      relatedSymbols,
        Timeframe         timeframe,
        int               historyMaxDays,
        TimeSpan          cacheTtl,
        CancellationToken ct)
    {
        var cacheKey = $"TrainerSelector:Corr:{targetSymbol}:{timeframe}:{string.Join(",", relatedSymbols)}";
        if (_cache.TryGetValue<Dictionary<string, double>>(cacheKey, out var cached))
            return cached!;

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-historyMaxDays);

        // Use D1 candles for correlation regardless of the training timeframe —
        // daily returns give a stable correlation estimate without excessive data.
        // Cap to most recent 120 D1 bars per symbol (6 symbols × 120 = 720 rows max)
        // to bound memory and query cost while keeping enough data for a stable estimate.
        const int maxCandlesPerSymbol = 120;
        var allSymbols = new List<string>(relatedSymbols.Count + 1) { targetSymbol };
        allSymbols.AddRange(relatedSymbols);

        var closes = await ctx.Set<Candle>()
            .Where(c => allSymbols.Contains(c.Symbol) &&
                        c.Timeframe == Timeframe.D1 &&
                        c.Timestamp >= cutoff &&
                        !c.IsDeleted)
            .OrderByDescending(c => c.Timestamp)
            .Take(maxCandlesPerSymbol * allSymbols.Count)
            .Select(c => new { c.Symbol, c.Timestamp, c.Close })
            .AsNoTracking()
            .ToListAsync(ct).ConfigureAwait(false);

        // Group by symbol → sorted list of (Date, Close)
        var bySymbol = closes
            .GroupBy(c => c.Symbol)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Timestamp).ToList());

        var result = new Dictionary<string, double>(relatedSymbols.Count);

        if (!bySymbol.TryGetValue(targetSymbol, out var targetCandles) || targetCandles.Count < 2)
        {
            // No candle data for target — fall back to flat discount
            foreach (var sym in relatedSymbols)
                result[sym] = CrossSymbolDiscountFactor;

            _cache.Set(cacheKey, result, cacheTtl);
            return result;
        }

        // Build target log-return series indexed by date
        var targetReturns = new Dictionary<DateTime, double>(targetCandles.Count - 1);
        for (int i = 1; i < targetCandles.Count; i++)
        {
            if (targetCandles[i - 1].Close > 0)
            {
                targetReturns[targetCandles[i].Timestamp.Date] =
                    Math.Log((double)(targetCandles[i].Close / targetCandles[i - 1].Close));
            }
        }

        foreach (var relSym in relatedSymbols)
        {
            if (!bySymbol.TryGetValue(relSym, out var relCandles) || relCandles.Count < 2)
            {
                result[relSym] = CrossSymbolDiscountFactor;
                continue;
            }

            // Build related log-return series and align on overlapping dates
            var alignedTarget = new List<double>();
            var alignedRel    = new List<double>();

            for (int i = 1; i < relCandles.Count; i++)
            {
                if (relCandles[i - 1].Close <= 0) continue;

                var date = relCandles[i].Timestamp.Date;
                if (targetReturns.TryGetValue(date, out var targetRet))
                {
                    alignedTarget.Add(targetRet);
                    alignedRel.Add(Math.Log((double)(relCandles[i].Close / relCandles[i - 1].Close)));
                }
            }

            if (alignedTarget.Count < MinCandlesForCorrelation)
            {
                result[relSym] = CrossSymbolDiscountFactor;
                continue;
            }

            double corr = PearsonCorrelation(alignedTarget, alignedRel);
            // Map |correlation| to a discount factor:
            // |corr| = 1.0 → discount 0.95 (highly correlated: very transferable)
            // |corr| = 0.5 → discount ~0.80
            // |corr| = 0.0 → discount 0.65 (uncorrelated: low transferability)
            double absCorr = Math.Abs(corr);
            result[relSym] = 0.65 + 0.30 * absCorr;
        }

        _cache.Set(cacheKey, result, cacheTtl);
        return result;
    }

    /// <summary>Pearson correlation coefficient for two equal-length sequences.</summary>
    private static double PearsonCorrelation(List<double> xs, List<double> ys)
    {
        int n = xs.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0, sumYY = 0;
        for (int i = 0; i < n; i++)
        {
            sumX  += xs[i];
            sumY  += ys[i];
            sumXY += xs[i] * ys[i];
            sumXX += xs[i] * xs[i];
            sumYY += ys[i] * ys[i];
        }

        double denom = Math.Sqrt((n * sumXX - sumX * sumX) * (n * sumYY - sumY * sumY));
        if (denom == 0) return 0;
        return (n * sumXY - sumX * sumY) / denom;
    }

    /// <summary>
    /// Computes the effective cross-symbol discount as a run-count-weighted average
    /// of per-symbol correlation-based discounts.
    /// </summary>
    private static double ComputeWeightedDiscount(
        List<ArchRunProjection>      runs,
        Dictionary<string, double>   correlationWeights)
    {
        double totalRuns    = 0;
        double weightedDisc = 0;

        foreach (var group in runs.GroupBy(r => r.Symbol))
        {
            string sym  = group.Key!;
            int    count = group.Count();
            double disc  = correlationWeights.GetValueOrDefault(sym, CrossSymbolDiscountFactor);

            weightedDisc += disc * count;
            totalRuns    += count;
        }

        return totalRuns > 0 ? weightedDisc / totalRuns : CrossSymbolDiscountFactor;
    }

    // ── Improvement #4: Graduated sample gate ──────────────────────────────

    /// <summary>
    /// Walks a ranked list and applies a continuous sample-count discount instead
    /// of a hard pass/fail gate. The discount is <c>min(1, (sampleCount / required)^0.5)</c>,
    /// with a hard floor below which the architecture is still rejected outright.
    /// Returns the architecture with the highest discounted score, or null.
    /// </summary>
    private LearnerArchitecture? PickBestWithGraduatedGate(
        List<(LearnerArchitecture Arch, double Score)> ranked,
        int       sampleCount,
        int       minSamplesStd,
        int       minSamplesDeep,
        Timeframe timeframe,
        double    hardFloorFraction)
    {
        double              bestScore = double.NegativeInfinity;
        LearnerArchitecture? best     = null;

        foreach (var (arch, rawScore) in ranked)
        {
            double discount = ComputeSampleDiscount(arch, sampleCount, minSamplesStd, minSamplesDeep, timeframe, hardFloorFraction);
            if (discount <= 0) continue; // below hard floor

            double adjustedScore = rawScore * discount;
            if (adjustedScore > bestScore)
            {
                bestScore = adjustedScore;
                best      = arch;
            }
        }

        if (best.HasValue)
        {
            _logger.LogDebug(
                "TrainerSelector: graduated gate selected {Arch} (adjustedScore={Score:F4}, samples={Samples})",
                best.Value, bestScore, sampleCount);
        }

        return best;
    }

    /// <summary>
    /// Returns a continuous discount factor in (0, 1] for the given architecture based on
    /// available sample count. SimpleTier always gets 1.0. Standard/Deep tier get a
    /// sqrt-ramped discount from the hard floor to 1.0.
    /// Returns 0 if below the hard floor (architecture should be rejected).
    /// </summary>
    private static double ComputeSampleDiscount(
        LearnerArchitecture arch,
        int       sampleCount,
        int       minSamplesStd,
        int       minSamplesDeep,
        Timeframe timeframe,
        double    hardFloorFraction)
    {
        if (SimpleTier.Contains(arch)) return 1.0;

        double scale = TimeframeSampleScale.GetValueOrDefault(timeframe, 1.0);
        int required;

        if (DeepTier.Contains(arch))
            required = (int)(minSamplesDeep * scale);
        else if (StandardTier.Contains(arch))
            required = (int)(minSamplesStd * scale);
        else
            return 0; // unclassified — reject

        if (required <= 0) return 1.0;

        int hardFloor = (int)(required * hardFloorFraction);
        if (sampleCount < hardFloor) return 0;

        double ratio = (double)sampleCount / required;
        return Math.Min(1.0, Math.Sqrt(ratio));
    }

    // ── Improvement #2: Drift-aware architecture boost ──────────────────────

    /// <summary>
    /// Computes per-architecture boost factors based on historical recovery from
    /// drift events. Architectures whose drift-triggered runs achieved above-average
    /// accuracy are boosted; those below average are not penalised (neutral 1.0).
    /// </summary>
    private async Task<Dictionary<LearnerArchitecture, double>> ComputeDriftAwareBoostsAsync(
        DbContext         ctx,
        string            symbol,
        Timeframe         timeframe,
        int               historyMaxDays,
        TimeSpan          cacheTtl,
        CancellationToken ct)
    {
        var cacheKey = $"TrainerSelector:DriftBoost:{symbol}:{timeframe}:{historyMaxDays}";
        if (_cache.TryGetValue<Dictionary<LearnerArchitecture, double>>(cacheKey, out var cached))
            return cached!;

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-historyMaxDays);

        // Load completed drift-triggered runs with their post-drift accuracy
        var driftRuns = await ctx.Set<MLTrainingRun>()
            .Where(r => r.Symbol == symbol &&
                        r.Timeframe == timeframe &&
                        r.Status == RunStatus.Completed &&
                        r.DirectionAccuracy.HasValue &&
                        r.DriftTriggerType != null &&
                        r.CompletedAt.HasValue &&
                        r.CompletedAt.Value >= cutoff)
            .AsNoTracking()
            .Select(r => new { r.LearnerArchitecture, r.DirectionAccuracy })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new Dictionary<LearnerArchitecture, double>();

        if (driftRuns.Count >= 3) // need enough data to be meaningful
        {
            double overallAvg = driftRuns.Average(r => (double)r.DirectionAccuracy!.Value);

            var perArch = driftRuns
                .GroupBy(r => r.LearnerArchitecture)
                .Where(g => g.Count() >= 2)
                .ToDictionary(g => g.Key, g => g.Average(r => (double)r.DirectionAccuracy!.Value));

            foreach (var (arch, avgAcc) in perArch)
            {
                // Only boost, never penalise — architectures above the drift-recovery
                // average get up to 15% boost proportional to their outperformance
                if (avgAcc > overallAvg)
                {
                    double outperformance = (avgAcc - overallAvg) / Math.Max(overallAvg, 0.01);
                    result[arch] = 1.0 + Math.Min(0.15, outperformance);
                }
            }
        }

        _cache.Set(cacheKey, result, cacheTtl);
        return result;
    }

    // ── Cache invalidation (for callers after training run completion) ─────

    /// <inheritdoc />
    public void InvalidateCache(string symbol, Timeframe timeframe)
    {
        // Evict the recent-runs cache for all history windows — the key
        // includes historyMaxDays which may vary by config, so use a
        // well-known set of plausible values plus the default.
        foreach (var days in new[] { 30, 60, DefaultHistoryMaxDays, 120, 180, 365 })
        {
            _cache.Remove($"TrainerSelector:RecentRuns:{symbol}:{timeframe}:{days}");
            // Improvement #2: also evict drift boost cache
            _cache.Remove($"TrainerSelector:DriftBoost:{symbol}:{timeframe}:{days}");
        }

        // Improvement #12: evict affinity cache so shadow data is picked up
        foreach (var regime in Enum.GetValues<MarketRegimeEnum>())
        {
            foreach (var days in new[] { 30, 60, DefaultHistoryMaxDays, 120, 180, 365 })
            foreach (var hours in new[] { 12, DefaultRegimeWindowHours, 48, 72 })
                _cache.Remove($"TrainerSelector:RawAffinity:{symbol}:{timeframe}:{regime}:{days}:{hours}");
        }

        _logger.LogDebug(
            "TrainerSelector: cache invalidated for {Symbol}/{Timeframe}",
            symbol, timeframe);
    }

    // ── Metrics ─────────────────────────────────────────────────────────────

    private void RecordSelectionMetric(
        LearnerArchitecture architecture,
        MarketRegimeEnum?   regime,
        string              role)
    {
        _metrics.MLArchitectureSelected.Add(1,
            new KeyValuePair<string, object?>("architecture", architecture.ToString()),
            new KeyValuePair<string, object?>("regime", regime?.ToString() ?? "unknown"),
            new KeyValuePair<string, object?>("role", role));
    }

    /// <summary>
    /// Records how deep into the fallback chain each selection went.
    /// Depth 1 = historical UCB1 (ideal), 5 = final BaggedLogistic fallback.
    /// Chronic depth-5 selections for a symbol indicate under-explored instruments.
    /// </summary>
    private void RecordFallbackDepthMetric(string symbol, Timeframe timeframe, int depth)
    {
        _metrics.MLSelectorFallbackDepth.Add(1,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", timeframe.ToString()),
            new KeyValuePair<string, object?>("depth", depth));

        if (depth >= 5)
        {
            _logger.LogWarning(
                "TrainerSelector: symbol {Symbol}/{Timeframe} fell through to final BaggedLogistic fallback — " +
                "consider adding training runs for this instrument",
                symbol, timeframe);
        }
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        _configCacheLock.Dispose();
        _affinityCacheLock.Dispose();
        _operatorDefaultLock.Dispose();
        _recentRunsCacheLock.Dispose();
    }

    // ── Projection type ─────────────────────────────────────────────────────

    private sealed class ArchRunProjection
    {
        public LearnerArchitecture LearnerArchitecture { get; init; }
        public decimal?            DirectionAccuracy   { get; init; }
        public decimal?            F1Score             { get; init; }
        public decimal?            SharpeRatio         { get; init; }
        public decimal?            ExpectedValue       { get; init; }
        public DateTime?           CompletedAt         { get; init; }
        public string?             Symbol              { get; init; }
        // Improvement #8: abstention-aware ranking
        public decimal?            AbstentionPrecision { get; init; }
        // Improvement #2: drift-aware selection (loaded for future per-drift-type
        // scoring; currently used only by ComputeDriftAwareBoostsAsync via separate query)
        public string?             DriftTriggerType    { get; init; }
    }
}
