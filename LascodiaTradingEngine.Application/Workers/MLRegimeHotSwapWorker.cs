using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects market regime transitions and reactivates previously superseded ML models
/// that demonstrated superior accuracy in the newly entered regime, providing immediate
/// coverage while a fresh training run is queued.
///
/// <b>Motivation:</b> When the market transitions from one regime to another (e.g.
/// Trending to Ranging), the current champion model may have been trained primarily on
/// data from the previous regime. A superseded model that performed well in the new
/// regime can serve as a fallback champion until a purpose-trained model is available.
///
/// <b>Algorithm (per cycle):</b>
/// <list type="number">
///   <item>Load all distinct (symbol, timeframe) pairs from active <see cref="MLModel"/> records.</item>
///   <item>For each pair, load the two most recent <see cref="MarketRegimeSnapshot"/> records.</item>
///   <item>If the regime changed (current != previous):
///     <list type="bullet">
///       <item>Load all superseded <see cref="MLModel"/> records for this symbol/timeframe
///             that have <c>ModelBytes</c> (deserializable weights).</item>
///       <item>Load <see cref="MLShadowRegimeBreakdown"/> rows for these models in the new regime.</item>
///       <item>Find the superseded model with the highest <c>ChallengerAccuracy</c> in the new regime.</item>
///       <item>Compare against the current champion's <c>DirectionAccuracy</c>.</item>
///       <item>If the superseded model's regime accuracy exceeds the champion's accuracy plus
///             <c>HotSwapAccuracyMargin</c>, promote it as a fallback champion and queue
///             a new training run.</item>
///     </list>
///   </item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLRegime:PollIntervalSeconds</c> — polling interval, default 60</item>
///   <item><c>MLRegime:EnableHotSwap</c>       — feature gate, default false</item>
///   <item><c>MLRegime:HotSwapAccuracyMargin</c> — minimum accuracy margin to trigger swap, default 0.05</item>
/// </list>
/// </summary>
public sealed class MLRegimeHotSwapWorker : BackgroundService
{
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    private const string CK_PollSecs       = "MLRegime:PollIntervalSeconds";
    private const string CK_EnableHotSwap  = "MLRegime:EnableHotSwap";
    private const string CK_AccuracyMargin = "MLRegime:HotSwapAccuracyMargin";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLRegimeHotSwapWorker>    _logger;

    /// <summary>
    /// Initialises the worker with a DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Factory for creating per-poll scoped service lifetimes, ensuring DbContexts
    /// are cleanly disposed after each cycle.
    /// </param>
    /// <param name="logger">Structured logger for hot-swap diagnostics.</param>
    public MLRegimeHotSwapWorker(
        IServiceScopeFactory            scopeFactory,
        ILogger<MLRegimeHotSwapWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs an infinite polling loop that checks
    /// for regime transitions and hot-swaps models when a better historical candidate
    /// exists for the new regime.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled on host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLRegimeHotSwapWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 60;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var rCtx    = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Read interval live so operators can adjust frequency without restart.
                pollSecs = await GetConfigAsync<int>(rCtx, CK_PollSecs, 60, stoppingToken);

                // Feature gate — skip all work when hot-swap is disabled.
                bool enabled = await GetConfigAsync<bool>(rCtx, CK_EnableHotSwap, false, stoppingToken);
                if (!enabled)
                {
                    _logger.LogDebug("MLRegimeHotSwapWorker: hot-swap disabled, sleeping.");
                    await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
                    continue;
                }

                await WorkerBulkhead.MLMonitoring.WaitAsync(stoppingToken);
                try
                {
                    await EvaluateRegimeTransitionsAsync(rCtx, wCtx, stoppingToken);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLRegimeHotSwapWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLRegimeHotSwapWorker stopping.");
    }

    // ── Core logic ────────────────────────────────────────────────────────────

    /// <summary>
    /// Iterates all distinct (symbol, timeframe) pairs with active models and checks
    /// whether a regime transition has occurred. When a transition is detected, attempts
    /// to hot-swap a superseded model with better regime-specific accuracy.
    /// </summary>
    private async Task EvaluateRegimeTransitionsAsync(
        DbContext         rCtx,
        DbContext         wCtx,
        CancellationToken ct)
    {
        double accuracyMargin = await GetConfigAsync<double>(rCtx, CK_AccuracyMargin, 0.05, ct);

        // Load distinct (symbol, timeframe) pairs from active models.
        var pairs = await rCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ProcessPairAsync(pair.Symbol, pair.Timeframe, accuracyMargin, rCtx, wCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RegimeHotSwap: error processing {Symbol}/{Tf} — skipping.",
                    pair.Symbol, pair.Timeframe);
            }
        }
    }

    /// <summary>
    /// Processes a single (symbol, timeframe) pair: detects regime transition, finds
    /// the best superseded model for the new regime, and hot-swaps if warranted.
    /// </summary>
    private async Task ProcessPairAsync(
        string            symbol,
        Timeframe         timeframe,
        double            accuracyMargin,
        DbContext         rCtx,
        DbContext         wCtx,
        CancellationToken ct)
    {
        // Load the two most recent regime snapshots for this symbol/timeframe.
        var recentRegimes = await rCtx.Set<MarketRegimeSnapshot>()
            .Where(r => r.Symbol == symbol && r.Timeframe == timeframe && !r.IsDeleted)
            .OrderByDescending(r => r.DetectedAt)
            .Take(2)
            .AsNoTracking()
            .ToListAsync(ct);

        if (recentRegimes.Count < 2)
            return; // Not enough history to detect a transition.

        var currentRegime  = recentRegimes[0].Regime;
        var previousRegime = recentRegimes[1].Regime;

        if (currentRegime == previousRegime)
            return; // No regime transition — nothing to do.

        _logger.LogInformation(
            "RegimeHotSwap: {Symbol}/{Tf} regime transition detected: {Prev} -> {Curr}.",
            symbol, timeframe, previousRegime, currentRegime);

        // Load the current champion model.
        var champion = await rCtx.Set<MLModel>()
            .Where(m => m.Symbol == symbol &&
                        m.Timeframe == timeframe &&
                        m.IsActive &&
                        !m.IsFallbackChampion &&
                        !m.IsDeleted)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (champion is null)
        {
            _logger.LogDebug(
                "RegimeHotSwap: {Symbol}/{Tf} no primary champion found — skipping.",
                symbol, timeframe);
            return;
        }

        double championAccuracy = (double)(champion.DirectionAccuracy ?? 0m);

        // Load all superseded models for this symbol/timeframe that have ModelBytes.
        var supersededModels = await rCtx.Set<MLModel>()
            .Where(m => m.Symbol == symbol &&
                        m.Timeframe == timeframe &&
                        m.Status == MLModelStatus.Superseded &&
                        m.ModelBytes != null &&
                        !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        if (supersededModels.Count == 0)
        {
            _logger.LogDebug(
                "RegimeHotSwap: {Symbol}/{Tf} no superseded models with weights — skipping.",
                symbol, timeframe);
            return;
        }

        var supersededModelIds = supersededModels.Select(m => m.Id).ToList();

        // Load shadow regime breakdowns for these superseded models in the new regime.
        // MLShadowRegimeBreakdown is linked via ShadowEvaluation.ChallengerModelId.
        var regimeBreakdowns = await rCtx.Set<MLShadowRegimeBreakdown>()
            .Where(b => b.Regime == currentRegime && !b.IsDeleted)
            .Join(
                rCtx.Set<MLShadowEvaluation>()
                    .Where(e => supersededModelIds.Contains(e.ChallengerModelId) &&
                                e.CompletedAt != null &&
                                !e.IsDeleted),
                b => b.ShadowEvaluationId,
                e => e.Id,
                (b, e) => new { e.ChallengerModelId, b.ChallengerAccuracy, b.TotalPredictions })
            .AsNoTracking()
            .ToListAsync(ct);

        if (regimeBreakdowns.Count == 0)
        {
            _logger.LogDebug(
                "RegimeHotSwap: {Symbol}/{Tf} no regime breakdowns for {Regime} — skipping.",
                symbol, timeframe, currentRegime);
            return;
        }

        // Find the superseded model with the highest ChallengerAccuracy in the new regime.
        // If a model has multiple evaluations, take the best one.
        var bestCandidate = regimeBreakdowns
            .GroupBy(b => b.ChallengerModelId)
            .Select(g => new
            {
                ModelId         = g.Key,
                BestAccuracy    = (double)g.Max(x => x.ChallengerAccuracy),
                TotalPredictions = g.Sum(x => x.TotalPredictions)
            })
            .OrderByDescending(x => x.BestAccuracy)
            .FirstOrDefault();

        if (bestCandidate is null)
            return;

        _logger.LogDebug(
            "RegimeHotSwap: {Symbol}/{Tf} best superseded model {ModelId} " +
            "has {Regime} accuracy {Acc:P1} vs champion {ChampAcc:P1} (margin={Margin:P1}).",
            symbol, timeframe, bestCandidate.ModelId,
            currentRegime, bestCandidate.BestAccuracy, championAccuracy, accuracyMargin);

        // Check if the superseded model beats the champion by the required margin.
        if (bestCandidate.BestAccuracy <= championAccuracy + accuracyMargin)
        {
            _logger.LogDebug(
                "RegimeHotSwap: {Symbol}/{Tf} superseded model {ModelId} does not beat " +
                "champion by required margin — no swap.",
                symbol, timeframe, bestCandidate.ModelId);
            return;
        }

        // Hot-swap: activate the superseded model as fallback champion.
        // Guard with Status == Superseded to prevent double-activation from concurrent workers.
        int updated = await wCtx.Set<MLModel>()
            .Where(m => m.Id == bestCandidate.ModelId &&
                        m.Status == MLModelStatus.Superseded &&
                        !m.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsFallbackChampion, true)
                .SetProperty(m => m.IsActive, true)
                .SetProperty(m => m.Status, MLModelStatus.Active),
                ct);

        if (updated == 0)
        {
            _logger.LogWarning(
                "RegimeHotSwap: {Symbol}/{Tf} failed to update model {ModelId} — row may have been deleted.",
                symbol, timeframe, bestCandidate.ModelId);
            return;
        }

        _logger.LogInformation(
            "RegimeHotSwap: {Symbol}/{Tf} hot-swapped model {ModelId} as fallback champion " +
            "(regime={Regime}, regimeAcc={Acc:P1}, championAcc={ChampAcc:P1}).",
            symbol, timeframe, bestCandidate.ModelId,
            currentRegime, bestCandidate.BestAccuracy, championAccuracy);

        // Queue a new training run so a purpose-trained model replaces the fallback.
        var trainingRun = new MLTrainingRun
        {
            Symbol      = symbol,
            Timeframe   = timeframe,
            TriggerType = TriggerType.Scheduled,
            Status      = RunStatus.Queued,
            FromDate    = DateTime.UtcNow.AddDays(-365),
            ToDate      = DateTime.UtcNow,
            StartedAt   = DateTime.UtcNow,
            Priority    = 1, // Drift-triggered priority — higher than routine.
        };

        wCtx.Set<MLTrainingRun>().Add(trainingRun);
        await wCtx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "RegimeHotSwap: {Symbol}/{Tf} queued training run {RunId} for regime {Regime}.",
            symbol, timeframe, trainingRun.Id, currentRegime);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key is absent or the stored
    /// string cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        DbContext         ctx,
        string            key,
        T                 defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
