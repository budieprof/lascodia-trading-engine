using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Resolves the active ML model for a given symbol/timeframe, with regime-aware
/// routing and suppression fallback. Extracted from <see cref="MLSignalScorer"/>
/// for testability and single-responsibility.
/// </summary>
internal sealed class MLModelResolver
{
    private static readonly TimeSpan DbQueryTimeout = TimeSpan.FromSeconds(5);

    private readonly IReadApplicationDbContext _context;
    private readonly ILogger _logger;
    private readonly SignalAbTestCoordinator? _abTestCoordinator;

    internal MLModelResolver(
        IReadApplicationDbContext context,
        ILogger logger,
        SignalAbTestCoordinator? abTestCoordinator = null)
    {
        _context           = context;
        _logger            = logger;
        _abTestCoordinator = abTestCoordinator;
    }

    internal async Task<(MLModel? Model, string? CurrentRegime, ModelRole Role)> ResolveActiveModelAsync(
        TradeSignal signal, Timeframe signalTimeframe, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();
        string? currentRegime = null;
        try
        {
            using var regimeCts = CreateLinkedTimeout(cancellationToken);
            var regimeSnap = await db.Set<MarketRegimeSnapshot>()
                .AsNoTracking()
                .Where(r => r.Symbol == signal.Symbol &&
                            r.Timeframe == signalTimeframe &&
                            !r.IsDeleted)
                .OrderByDescending(r => r.DetectedAt)
                .FirstOrDefaultAsync(regimeCts.Token);

            currentRegime = regimeSnap?.Regime.ToString();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Regime lookup timed out for {Symbol}/{Tf} — using global model",
                signal.Symbol, signalTimeframe);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Regime lookup failed for {Symbol}/{Tf} — using global model",
                signal.Symbol, signalTimeframe);
        }

        if (_abTestCoordinator?.GetActiveTest(signal.Symbol, signalTimeframe) is { } activeTest)
        {
            var routedModelId = _abTestCoordinator.ResolveModelForSignal(
                signal.StrategyId,
                activeTest.ChampionModelId,
                activeTest.ChallengerModelId);
            var role = routedModelId == activeTest.ChallengerModelId
                ? ModelRole.Challenger
                : ModelRole.Champion;

            var routed = await db.Set<MLModel>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.Id == routedModelId &&
                    x.Symbol == signal.Symbol &&
                    x.Timeframe == signalTimeframe &&
                    !x.IsDeleted &&
                    x.Status != MLModelStatus.Failed,
                    cancellationToken);

            if (routed?.ModelBytes is { Length: > 0 } && !routed.IsSuppressed)
            {
                _logger.LogDebug(
                    "A/B routing {Symbol}/{Tf} strategy {StrategyId}: {Role} model {ModelId}",
                    signal.Symbol, signalTimeframe, signal.StrategyId, role, routed.Id);
                return (routed, currentRegime, role);
            }

            _logger.LogWarning(
                "A/B routing selected unavailable {Role} model {ModelId} for {Symbol}/{Tf}; signal proceeds unscored until test is repaired or ended",
                role, routedModelId, signal.Symbol, signalTimeframe);
            return (null, currentRegime, role);
        }

        MLModel? model = null;
        if (currentRegime is not null)
        {
            model = await db.Set<MLModel>()
                .AsNoTracking()
                .Where(x => x.Symbol      == signal.Symbol &&
                            x.Timeframe   == signalTimeframe &&
                            x.RegimeScope == currentRegime &&
                            x.IsActive    &&
                            !x.IsFallbackChampion &&
                            !x.IsDeleted)
                .OrderByDescending(x => x.ExpectedValue ?? -1m)
                .ThenByDescending(x => x.ActivatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (model is null)
        {
            model = await db.Set<MLModel>()
                .AsNoTracking()
                .Where(x => x.Symbol      == signal.Symbol &&
                            x.Timeframe   == signalTimeframe &&
                            x.RegimeScope == null &&
                            x.IsActive    &&
                            !x.IsFallbackChampion &&
                            !x.IsDeleted)
                .OrderByDescending(x => x.ExpectedValue ?? -1m)
                .ThenByDescending(x => x.ActivatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (model?.ModelBytes is not { Length: > 0 })
        {
            _logger.LogDebug(
                "No active ML model for {Symbol}/{Tf} — signal proceeds unscored",
                signal.Symbol, signalTimeframe);
            return (null, currentRegime, ModelRole.Champion);
        }

        if (model.IsSuppressed)
        {
            MLModel? fallback = null;
            try
            {
                using var fbCts = CreateLinkedTimeout(cancellationToken);
                fallback = await db.Set<MLModel>()
                    .AsNoTracking()
                    .Where(x => x.Symbol           == signal.Symbol      &&
                                x.Timeframe        == signalTimeframe     &&
                                x.IsFallbackChampion                      &&
                                x.IsActive         &&
                                !x.IsSuppressed    &&
                                !x.IsDeleted)
                    .OrderByDescending(x => x.ExpectedValue ?? -1m)
                    .ThenByDescending(x => x.ActivatedAt)
                    .FirstOrDefaultAsync(fbCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Fallback champion lookup timed out for {Symbol}/{Tf}",
                    signal.Symbol, signalTimeframe);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Fallback champion lookup failed for {Symbol}/{Tf}",
                    signal.Symbol, signalTimeframe);
            }

            if (fallback?.ModelBytes is not { Length: > 0 })
            {
                _logger.LogDebug(
                    "Scoring suppressed for {Symbol}/{Tf} model {Id} — no fallback champion available.",
                    signal.Symbol, signalTimeframe, model.Id);
                return (null, currentRegime, ModelRole.Champion);
            }

            _logger.LogDebug(
                "Scoring suppressed for {Symbol}/{Tf} primary model {Id} — " +
                "routing to fallback champion {FbId}.",
                signal.Symbol, signalTimeframe, model.Id, fallback.Id);
            model = fallback;
        }

        return (model, currentRegime, ModelRole.Champion);
    }

    // ── Improvement #2: Bulk model resolution for batch scoring ──────────

    /// <summary>
    /// Resolves active models for a batch of (symbol, timeframe) pairs in a small,
    /// fixed number of DB round-trips (3 queries: regime snapshots, active models,
    /// fallback champions for the suppressed subset) instead of <c>N × 2</c> per-
    /// signal round-trips.
    /// </summary>
    /// <remarks>
    /// Follows the same precedence rules as <see cref="ResolveActiveModelAsync"/>:
    /// regime-scoped model preferred over global, suppressed primary routed to the
    /// fallback champion. Signals that resolve to no model (or to a suppressed model
    /// with no fallback) are returned with <c>Model = null</c>.
    /// </remarks>
    internal async Task<IReadOnlyDictionary<(string Symbol, Timeframe Tf), (MLModel? Model, string? CurrentRegime, ModelRole Role)>>
        ResolveActiveModelsBatchAsync(
            IReadOnlyList<(TradeSignal Signal, Timeframe Tf)> inputs,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, Timeframe), (MLModel?, string?, ModelRole)>();
        if (inputs.Count == 0) return result;

        var db = _context.GetDbContext();

        var distinctPairs = inputs
            .Select(i => (i.Signal.Symbol, i.Tf))
            .Distinct()
            .ToList();
        var symbols = distinctPairs.Select(p => p.Symbol).Distinct().ToArray();
        var tfs     = distinctPairs.Select(p => p.Tf).Distinct().ToArray();

        // Step 1: latest regime snapshot per (symbol, tf). One query.
        var regimeMap = new Dictionary<(string, Timeframe), string?>();
        try
        {
            using var regimeCts = CreateLinkedTimeout(cancellationToken);
            var regimeRows = await db.Set<MarketRegimeSnapshot>()
                .AsNoTracking()
                .Where(r => symbols.Contains(r.Symbol) &&
                            tfs.Contains(r.Timeframe) &&
                            !r.IsDeleted)
                .GroupBy(r => new { r.Symbol, r.Timeframe })
                .Select(g => g.OrderByDescending(x => x.DetectedAt).First())
                .Select(r => new { r.Symbol, r.Timeframe, r.Regime })
                .ToListAsync(regimeCts.Token);

            foreach (var row in regimeRows)
                regimeMap[(row.Symbol, row.Timeframe)] = row.Regime.ToString();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Batch regime lookup timed out — using global models for batch");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Batch regime lookup failed — using global models for batch");
        }

        // Step 2: all active candidate models for the input symbols/timeframes. One query.
        // Filter in-memory per-pair by regime-scope precedence. The upper bound
        // (count ≤ distinctPairs.Count × 4) keeps the payload small.
        var candidates = await db.Set<MLModel>()
            .AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol) &&
                        tfs.Contains(x.Timeframe) &&
                        x.IsActive &&
                        !x.IsFallbackChampion &&
                        !x.IsDeleted &&
                        x.ModelBytes != null)
            .OrderByDescending(x => x.ExpectedValue ?? -1m)
            .ThenByDescending(x => x.ActivatedAt)
            .ToListAsync(cancellationToken);

        var suppressedPrimaries = new List<(string Symbol, Timeframe Tf, MLModel Primary)>();

        foreach (var (symbol, tf) in distinctPairs)
        {
            regimeMap.TryGetValue((symbol, tf), out var currentRegime);

            var firstSignalForPair = inputs.First(i => i.Signal.Symbol == symbol && i.Tf == tf).Signal;
            if (_abTestCoordinator?.GetActiveTest(symbol, tf) is { } activeTest)
            {
                var routedModelId = _abTestCoordinator.ResolveModelForSignal(
                    firstSignalForPair.StrategyId,
                    activeTest.ChampionModelId,
                    activeTest.ChallengerModelId);
                var role = routedModelId == activeTest.ChallengerModelId
                    ? ModelRole.Challenger
                    : ModelRole.Champion;

                var routed = await db.Set<MLModel>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.Id == routedModelId &&
                        x.Symbol == symbol &&
                        x.Timeframe == tf &&
                        !x.IsDeleted &&
                        x.Status != MLModelStatus.Failed,
                        cancellationToken);

                result[(symbol, tf)] = routed?.ModelBytes is { Length: > 0 } && !routed.IsSuppressed
                    ? (routed, currentRegime, role)
                    : (null, currentRegime, role);
                continue;
            }

            // Prefer regime-scoped match; fall back to RegimeScope==null (global).
            MLModel? picked = null;
            if (currentRegime is not null)
            {
                picked = candidates.FirstOrDefault(x =>
                    x.Symbol == symbol && x.Timeframe == tf && x.RegimeScope == currentRegime);
            }
            picked ??= candidates.FirstOrDefault(x =>
                x.Symbol == symbol && x.Timeframe == tf && x.RegimeScope == null);

            if (picked is null)
            {
                result[(symbol, tf)] = (null, currentRegime, ModelRole.Champion);
                continue;
            }

            if (picked.IsSuppressed)
            {
                suppressedPrimaries.Add((symbol, tf, picked));
                // Placeholder; step 3 fills in fallback or null.
                result[(symbol, tf)] = (null, currentRegime, ModelRole.Champion);
            }
            else
            {
                result[(symbol, tf)] = (picked, currentRegime, ModelRole.Champion);
            }
        }

        // Step 3: fallback champions for the suppressed subset. One query (skipped when empty).
        if (suppressedPrimaries.Count > 0)
        {
            var suppSyms = suppressedPrimaries.Select(s => s.Symbol).Distinct().ToArray();
            var suppTfs  = suppressedPrimaries.Select(s => s.Tf).Distinct().ToArray();

            List<MLModel> fallbacks = [];
            try
            {
                using var fbCts = CreateLinkedTimeout(cancellationToken);
                fallbacks = await db.Set<MLModel>()
                    .AsNoTracking()
                    .Where(x => suppSyms.Contains(x.Symbol) &&
                                suppTfs.Contains(x.Timeframe) &&
                                x.IsFallbackChampion &&
                                x.IsActive &&
                                !x.IsSuppressed &&
                                !x.IsDeleted &&
                                x.ModelBytes != null)
                    .OrderByDescending(x => x.ExpectedValue ?? -1m)
                    .ThenByDescending(x => x.ActivatedAt)
                    .ToListAsync(fbCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Batch fallback champion lookup timed out");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Batch fallback champion lookup failed");
            }

            foreach (var (symbol, tf, primary) in suppressedPrimaries)
            {
                regimeMap.TryGetValue((symbol, tf), out var currentRegime);
                var fb = fallbacks.FirstOrDefault(x => x.Symbol == symbol && x.Timeframe == tf);
                if (fb is null)
                {
                    _logger.LogDebug(
                        "Batch: scoring suppressed for {Symbol}/{Tf} model {Id} — no fallback champion",
                        symbol, tf, primary.Id);
                    result[(symbol, tf)] = (null, currentRegime, ModelRole.Champion);
                }
                else
                {
                    _logger.LogDebug(
                        "Batch: suppressed primary {Id} for {Symbol}/{Tf} → fallback {FbId}",
                        primary.Id, symbol, tf, fb.Id);
                    result[(symbol, tf)] = (fb, currentRegime, ModelRole.Champion);
                }
            }
        }

        return result;
    }

    // ── Improvement #1: Ensemble scoring committee ────────────────────────

    /// <summary>
    /// Resolves up to <paramref name="maxSize"/> active models from different
    /// <c>ModelFamily</c> groups for the given symbol/timeframe. The primary
    /// model (resolved by <see cref="ResolveActiveModelAsync"/>) is always
    /// included as the first element. Additional committee members are drawn
    /// from non-suppressed, non-fallback active models with different
    /// <see cref="MLModel.LearnerArchitecture"/> family classifications.
    /// </summary>
    /// <returns>
    /// A list of (Model, CurrentRegime) tuples. Empty if no active model exists.
    /// The first element is always the primary model.
    /// </returns>
    internal async Task<List<(MLModel Model, string? CurrentRegime, ModelRole Role)>> ResolveCommitteeModelsAsync(
        TradeSignal signal, Timeframe signalTimeframe, int maxSize, CancellationToken ct)
    {
        var (primary, regime, role) = await ResolveActiveModelAsync(signal, signalTimeframe, ct);
        if (primary is null)
            return [];

        var result = new List<(MLModel, string?, ModelRole)>(maxSize) { (primary, regime, role) };
        if (maxSize <= 1 || role == ModelRole.Challenger)
            return result;

        // Load all active, non-suppressed global models for this symbol/timeframe
        var db = _context.GetDbContext();
        var candidates = await db.Set<MLModel>()
            .AsNoTracking()
            .Where(x => x.Symbol    == signal.Symbol &&
                        x.Timeframe == signalTimeframe &&
                        x.IsActive  &&
                        !x.IsSuppressed &&
                        !x.IsFallbackChampion &&
                        !x.IsDeleted &&
                        x.Id != primary.Id &&
                        x.ModelBytes != null)
            .OrderByDescending(x => x.ExpectedValue ?? -1m)
            .ThenByDescending(x => x.ActivatedAt)
            .Take(10) // reasonable upper bound to prevent large scans
            .ToListAsync(ct);

        // Select candidates from different model families than the primary
        var usedFamilies = new HashSet<int> { FamilyOf(primary.LearnerArchitecture) };

        foreach (var candidate in candidates)
        {
            if (result.Count >= maxSize) break;
            if (candidate.ModelBytes is not { Length: > 0 }) continue;

            int family = FamilyOf(candidate.LearnerArchitecture);
            if (!usedFamilies.Add(family)) continue;

            result.Add((candidate, regime, ModelRole.Champion));
        }

        return result;
    }

    /// <summary>
    /// Maps a <see cref="LearnerArchitecture"/> to its model family integer.
    /// Mirrors the <c>ModelFamily</c> enum in <c>TrainerSelector</c>.
    /// </summary>
    private static int FamilyOf(LearnerArchitecture arch) => arch switch
    {
        LearnerArchitecture.BaggedLogistic  => 0, // BaggedEnsemble
        LearnerArchitecture.Elm             => 0,
        LearnerArchitecture.Smote           => 0,
        LearnerArchitecture.Gbm             => 1, // TreeBoosting
        LearnerArchitecture.AdaBoost        => 1,
        LearnerArchitecture.QuantileRf      => 1,
        LearnerArchitecture.Rocket          => 2, // ConvKernel
        LearnerArchitecture.TemporalConvNet => 2,
        LearnerArchitecture.FtTransformer   => 3, // Transformer
        LearnerArchitecture.TabNet          => 3,
        LearnerArchitecture.Svgp            => 4, // GaussianProcess
        LearnerArchitecture.Dann            => 5, // DomainAdaptation
        _                                   => 99,
    };

    private static CancellationTokenSource CreateLinkedTimeout(CancellationToken parent)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        cts.CancelAfter(DbQueryTimeout);
        return cts;
    }
}
