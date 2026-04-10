using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically evaluates running signal-level A/B tests between champion and challenger
/// ML models. For each active test, loads resolved trades attributed to each arm, computes
/// per-model P&amp;L metrics (net P&amp;L, Sharpe, win rate), and runs SPRT on the cumulative
/// P&amp;L difference to reach a statistically grounded promotion decision.
///
/// <para>This worker bridges the gap between accuracy-level shadow evaluation (which tests
/// whether a model predicts direction correctly) and real trading performance (which includes
/// slippage, spread, position sizing, and signal filtering effects).</para>
///
/// <para><b>Poll interval:</b> 30 minutes (configurable via <c>AbTest:PollIntervalSeconds</c>).</para>
///
/// <para><b>Decision flow:</b>
/// <list type="number">
///   <item>Load all active A/B tests from <c>EngineConfig</c>.</item>
///   <item>For each test, query closed <see cref="Position"/> records whose opening
///         <see cref="Order"/> was tagged with a <see cref="TradeSignal"/> scored by the
///         champion or challenger model (via <see cref="MLModelPredictionLog"/>).</item>
///   <item>Compute per-arm metrics and run SPRT.</item>
///   <item>If challenger wins: activate challenger, demote champion, end test.</item>
///   <item>If champion wins: reject challenger, end test.</item>
///   <item>If inconclusive + max duration exceeded: keep champion, end test.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MLSignalAbTestWorker : BackgroundService
{
    private const string CK_PollSecs              = "AbTest:PollIntervalSeconds";
    private const string CK_MinTradesPerArm       = "AbTest:MinTradesPerArm";
    private const string CK_MaxDurationDays       = "AbTest:MaxDurationDays";
    private const string CK_MaxConcurrentPerSymbol = "AbTest:MaxConcurrentPerSymbol";

    private const int DefaultPollSeconds            = 1800; // 30 minutes
    private const int DefaultMinTradesPerArm        = 30;
    private const int DefaultMaxDurationDays        = 14;
    private const int DefaultMaxConcurrentPerSymbol = 3;

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLSignalAbTestWorker>     _logger;
    private readonly SignalAbTestCoordinator            _coordinator;

    public MLSignalAbTestWorker(
        IServiceScopeFactory              scopeFactory,
        ILogger<MLSignalAbTestWorker>     logger,
        SignalAbTestCoordinator            coordinator)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _coordinator  = coordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSignalAbTestWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSeconds;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var readDb      = readContext.GetDbContext();
                var writeDb     = writeContext.GetDbContext();

                pollSecs = await GetConfigAsync<int>(readDb, CK_PollSecs, DefaultPollSeconds, stoppingToken);

                // Refresh the coordinator's in-memory cache from the database
                await _coordinator.RefreshActiveCacheAsync(readContext, stoppingToken);

                // Process all active A/B tests
                await ProcessActiveTestsAsync(readDb, writeDb, writeContext, readContext, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLSignalAbTestWorker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLSignalAbTestWorker stopping.");
    }

    // ── Main processing loop ────────────────────────────────────────────────

    private async Task ProcessActiveTestsAsync(
        DbContext readDb,
        DbContext writeDb,
        IWriteApplicationDbContext writeContext,
        IReadApplicationDbContext readContext,
        CancellationToken ct)
    {
        int minTrades    = await GetConfigAsync<int>(readDb, CK_MinTradesPerArm, DefaultMinTradesPerArm, ct);
        int maxDuration  = await GetConfigAsync<int>(readDb, CK_MaxDurationDays, DefaultMaxDurationDays, ct);

        // Load all active A/B test config entries
        var activeConfigs = await readDb.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("AbTest:Active:") && !c.Key.Contains(":Meta:"))
            .ToListAsync(ct);

        if (activeConfigs.Count == 0)
        {
            _logger.LogDebug("No active A/B tests to evaluate.");
            return;
        }

        _logger.LogDebug("Evaluating {Count} active A/B test(s).", activeConfigs.Count);

        foreach (var config in activeConfigs)
        {
            try
            {
                await ProcessSingleTestAsync(config, readDb, writeDb, writeContext, readContext,
                    minTrades, maxDuration, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing A/B test {Key}.", config.Key);
            }
        }
    }

    private async Task ProcessSingleTestAsync(
        EngineConfig config,
        DbContext readDb,
        DbContext writeDb,
        IWriteApplicationDbContext writeContext,
        IReadApplicationDbContext readContext,
        int minTrades,
        int maxDuration,
        CancellationToken ct)
    {
        // Parse key: AbTest:Active:{championId}:{challengerId}
        var parts = config.Key.Split(':');
        if (parts.Length < 4 ||
            !long.TryParse(parts[2], out var championId) ||
            !long.TryParse(parts[3], out var challengerId))
        {
            _logger.LogWarning("Malformed A/B test key: {Key}", config.Key);
            return;
        }

        var symbol = config.Value ?? string.Empty;
        var metaPrefix = $"AbTest:Meta:{championId}:{challengerId}:";

        // Load metadata
        var metaEntries = await readDb.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith(metaPrefix))
            .ToDictionaryAsync(c => c.Key, c => c.Value ?? string.Empty, ct);

        if (!metaEntries.TryGetValue(metaPrefix + "Timeframe", out var tfStr) ||
            !Enum.TryParse<Timeframe>(tfStr, out var timeframe))
        {
            _logger.LogWarning("Missing or invalid timeframe metadata for A/B test {Key}", config.Key);
            return;
        }

        if (!metaEntries.TryGetValue(metaPrefix + "StartedAtUtc", out var startStr) ||
            !DateTime.TryParse(startStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var startedAt))
        {
            startedAt = DateTime.UtcNow.AddDays(-1); // Fallback
        }

        // ── Guard: check if challenger model is still alive ─────────────────
        var challengerModel = await readDb.Set<MLModel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == challengerId && !m.IsDeleted, ct);

        if (challengerModel is null || challengerModel.IsSuppressed ||
            challengerModel.Status == MLModelStatus.Failed)
        {
            _logger.LogInformation(
                "Challenger model {ChallengerId} is retired/suppressed/deleted. " +
                "Auto-ending A/B test with KeepChampion for {Symbol}/{Timeframe}.",
                challengerId, symbol, timeframe);

            await _coordinator.EndAbTestAsync(championId, challengerId, symbol, timeframe, writeContext, ct);
            return;
        }

        // ── Load resolved trade outcomes per arm ────────────────────────────
        var state = await BuildAbTestStateAsync(
            readDb, championId, challengerId, symbol, timeframe, startedAt, ct);

        // ── Evaluate SPRT ───────────────────────────────────────────────────
        var result = _coordinator.Evaluate(state, minTrades, maxDuration);

        _logger.LogInformation(
            "A/B test {Symbol}/{Timeframe} (champion={Champion}, challenger={Challenger}): " +
            "Decision={Decision}, ChampionTrades={ChampTrades}, ChallengerTrades={ChallTrades}, " +
            "ChampionAvgPnL={ChampAvg:F4}, ChallengerAvgPnL={ChallAvg:F4}, " +
            "ChampionSharpe={ChampSharpe:F3}, ChallengerSharpe={ChallSharpe:F3}, " +
            "SPRT_LLR={LLR:F3}. {Reason}",
            symbol, timeframe, championId, challengerId,
            result.Decision, result.ChampionTradeCount, result.ChallengerTradeCount,
            result.ChampionAvgPnl, result.ChallengerAvgPnl,
            result.ChampionSharpe, result.ChallengerSharpe,
            result.SprtLogLikelihoodRatio, result.Reason);

        // ── Act on decision ─────────────────────────────────────────────────
        switch (result.Decision)
        {
            case AbTestDecision.PromoteChallenger:
                await PromoteChallengerAsync(writeDb, championId, challengerId, symbol, timeframe, ct);
                await _coordinator.EndAbTestAsync(championId, challengerId, symbol, timeframe, writeContext, ct);
                break;

            case AbTestDecision.KeepChampion:
                await RejectChallengerAsync(writeDb, challengerId, ct);
                await _coordinator.EndAbTestAsync(championId, challengerId, symbol, timeframe, writeContext, ct);
                break;

            case AbTestDecision.Inconclusive:
                // Test continues — no action needed
                break;
        }
    }

    // ── Build test state from resolved trades ───────────────────────────────

    /// <summary>
    /// Queries closed positions and their associated prediction logs to build the
    /// full <see cref="AbTestState"/> for evaluation. The join path is:
    /// Position → OpenOrderId → Order → TradeSignalId → TradeSignal → MLModelId,
    /// cross-referenced with MLModelPredictionLog for the model attribution.
    /// </summary>
    private async Task<AbTestState> BuildAbTestStateAsync(
        DbContext readDb,
        long championId,
        long challengerId,
        string symbol,
        Timeframe timeframe,
        DateTime startedAt,
        CancellationToken ct)
    {
        // Find all closed positions for this symbol since the test started,
        // joined to their opening order and trade signal for model attribution.
        var closedPositions = await readDb.Set<Position>()
            .AsNoTracking()
            .Where(p => p.Symbol == symbol &&
                        p.Status == PositionStatus.Closed &&
                        !p.IsDeleted &&
                        p.ClosedAt != null &&
                        p.ClosedAt >= startedAt &&
                        p.OpenOrderId != null)
            .Select(p => new
            {
                p.Id,
                p.RealizedPnL,
                p.OpenedAt,
                p.ClosedAt,
                p.OpenOrderId,
            })
            .ToListAsync(ct);

        if (closedPositions.Count == 0)
        {
            return new AbTestState(0, championId, challengerId, symbol, timeframe, startedAt,
                new List<AbTestOutcome>(), new List<AbTestOutcome>());
        }

        // Get the order IDs to find associated trade signals
        var openOrderIds = closedPositions
            .Where(p => p.OpenOrderId.HasValue)
            .Select(p => p.OpenOrderId!.Value)
            .Distinct()
            .ToList();

        // Order → TradeSignalId mapping
        var orderSignalMap = await readDb.Set<Order>()
            .AsNoTracking()
            .Where(o => openOrderIds.Contains(o.Id) && o.TradeSignalId != null)
            .Select(o => new { o.Id, o.TradeSignalId })
            .ToDictionaryAsync(o => o.Id, o => o.TradeSignalId!.Value, ct);

        var signalIds = orderSignalMap.Values.Distinct().ToList();

        // TradeSignal → MLModelId mapping
        var signalModelMap = await readDb.Set<TradeSignal>()
            .AsNoTracking()
            .Where(s => signalIds.Contains(s.Id) && s.MLModelId != null)
            .Select(s => new { s.Id, s.MLModelId })
            .ToDictionaryAsync(s => s.Id, s => s.MLModelId!.Value, ct);

        // Also check MLModelPredictionLog for model attribution (more reliable for A/B tests
        // where model routing was explicitly tracked)
        var predictionModelMap = await readDb.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(pl => signalIds.Contains(pl.TradeSignalId) &&
                         (pl.MLModelId == championId || pl.MLModelId == challengerId))
            .Select(pl => new { pl.TradeSignalId, pl.MLModelId })
            .ToDictionaryAsync(pl => pl.TradeSignalId, pl => pl.MLModelId, ct);

        // Build outcomes per arm
        var champOutcomes = new List<AbTestOutcome>();
        var challOutcomes = new List<AbTestOutcome>();

        foreach (var pos in closedPositions)
        {
            if (!pos.OpenOrderId.HasValue) continue;
            if (!orderSignalMap.TryGetValue(pos.OpenOrderId.Value, out var signalId)) continue;

            // Prefer prediction log attribution, fall back to signal's MLModelId
            long? modelId = predictionModelMap.TryGetValue(signalId, out var predModelId)
                ? predModelId
                : signalModelMap.TryGetValue(signalId, out var sigModelId) ? sigModelId : null;

            if (modelId is null) continue;

            var durationMinutes = pos.ClosedAt.HasValue
                ? (int)(pos.ClosedAt.Value - pos.OpenedAt).TotalMinutes
                : 0;

            var outcome = new AbTestOutcome(
                Pnl:             (double)pos.RealizedPnL,
                Magnitude:       Math.Abs((double)pos.RealizedPnL),
                DurationMinutes: durationMinutes,
                ResolvedAtUtc:   pos.ClosedAt ?? DateTime.UtcNow);

            if (modelId == championId)
                champOutcomes.Add(outcome);
            else if (modelId == challengerId)
                challOutcomes.Add(outcome);
        }

        return new AbTestState(0, championId, challengerId, symbol, timeframe, startedAt,
            champOutcomes, challOutcomes);
    }

    // ── Promotion / rejection ───────────────────────────────────────────────

    /// <summary>
    /// Promotes the challenger model: activates it and demotes the champion to Superseded.
    /// </summary>
    private async Task PromoteChallengerAsync(
        DbContext writeDb,
        long championId,
        long challengerId,
        string symbol,
        Timeframe timeframe,
        CancellationToken ct)
    {
        // Demote champion
        await writeDb.Set<MLModel>()
            .Where(m => m.Id == championId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsActive, false)
                .SetProperty(m => m.Status, MLModelStatus.Superseded), ct);

        // Promote challenger
        await writeDb.Set<MLModel>()
            .Where(m => m.Id == challengerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsActive, true)
                .SetProperty(m => m.Status, MLModelStatus.Active)
                .SetProperty(m => m.ActivatedAt, DateTime.UtcNow)
                .SetProperty(m => m.PreviousChampionModelId, championId), ct);

        // Log lifecycle events
        writeDb.Set<MLModelLifecycleLog>().Add(new MLModelLifecycleLog
        {
            MLModelId                = challengerId,
            EventType                = "AbTestPromotion",
            NewStatus                = MLModelStatus.Active,
            PreviousChampionModelId  = championId,
            Reason                   = $"Promoted via signal-level A/B test. Previous champion: {championId}.",
            OccurredAt               = DateTime.UtcNow,
        });

        writeDb.Set<MLModelLifecycleLog>().Add(new MLModelLifecycleLog
        {
            MLModelId    = championId,
            EventType    = "AbTestDemotion",
            NewStatus    = MLModelStatus.Superseded,
            Reason       = $"Demoted by signal-level A/B test. New champion: {challengerId}.",
            OccurredAt   = DateTime.UtcNow,
        });

        await writeDb.SaveChangesAsync(ct);

        _logger.LogInformation(
            "A/B test promotion: model {Challenger} promoted to champion for {Symbol}/{Timeframe}. " +
            "Previous champion {Champion} demoted to Superseded.",
            challengerId, symbol, timeframe, championId);
    }

    /// <summary>
    /// Rejects the challenger model by marking it as Retired.
    /// </summary>
    private async Task RejectChallengerAsync(
        DbContext writeDb,
        long challengerId,
        CancellationToken ct)
    {
        await writeDb.Set<MLModel>()
            .Where(m => m.Id == challengerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, MLModelStatus.Superseded)
                .SetProperty(m => m.IsActive, false), ct);

        writeDb.Set<MLModelLifecycleLog>().Add(new MLModelLifecycleLog
        {
            MLModelId    = challengerId,
            EventType    = "AbTestRejection",
            NewStatus    = MLModelStatus.Superseded,
            Reason       = "Rejected by signal-level A/B test. Champion retained.",
            OccurredAt   = DateTime.UtcNow,
        });

        await writeDb.SaveChangesAsync(ct);

        _logger.LogInformation(
            "A/B test rejection: challenger model {Challenger} retired. Champion retained.",
            challengerId);
    }

    // ── Config helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/> or returns <paramref name="defaultValue"/>
    /// when the key is absent or the stored string cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        DbContext ctx,
        string    key,
        T         defaultValue,
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
