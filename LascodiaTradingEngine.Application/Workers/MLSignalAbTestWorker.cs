using System.Data;
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
///   <item>Load all active A/B tests from <see cref="MLSignalAbTest"/>.</item>
///   <item>For each test, query closed <see cref="Position"/> records whose opening
///         <see cref="Order"/> was tagged with a <see cref="TradeSignal"/> assigned to the
///         champion or challenger arm.</item>
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
    private const string CK_Alpha                 = "AbTest:Alpha";
    private const string CK_Beta                  = "AbTest:Beta";
    private const string CK_DeltaWinMultiplier    = "AbTest:DeltaWinSizeMultiplier";
    private const string CK_MinimumEffectPnl      = "AbTest:MinimumEffectPnl";
    private const string CK_WinsorizationQuantile = "AbTest:WinsorizationQuantile";
    private const string CK_MaxCovariateImbalance = "AbTest:MaxCovariateImbalance";
    private const string CK_ImbalanceEvidenceMultiplier = "AbTest:ImbalanceEvidenceMultiplier";
    private const int DefaultPollSeconds            = 1800; // 30 minutes
    private const int DefaultMinTradesPerArm        = 30;
    private const int DefaultMaxDurationDays        = 14;
    private const double DefaultAlpha               = 0.05;
    private const double DefaultBeta                = 0.20;
    private const double DefaultDeltaWinMultiplier  = 0.50;
    private const double DefaultMinimumEffectPnl    = 0.0;
    private const double DefaultWinsorizationQuantile = 0.05;
    private const double DefaultMaxCovariateImbalance = 0.35;
    private const double DefaultImbalanceEvidenceMultiplier = 1.5;

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLSignalAbTestWorker>     _logger;
    private readonly SignalAbTestCoordinator            _coordinator;
    private readonly IDistributedLock                   _distributedLock;
    private readonly ISignalAbTestStateBuilder          _stateBuilder;
    private readonly ISignalAbTestTerminalResultStore   _terminalResultStore;
    private readonly IMLModelLifecycleTransitionService _lifecycleTransitionService;

    public MLSignalAbTestWorker(
        IServiceScopeFactory              scopeFactory,
        ILogger<MLSignalAbTestWorker>     logger,
        SignalAbTestCoordinator            coordinator,
        IDistributedLock                   distributedLock,
        ISignalAbTestStateBuilder          stateBuilder,
        ISignalAbTestTerminalResultStore   terminalResultStore,
        IMLModelLifecycleTransitionService lifecycleTransitionService)
    {
        _scopeFactory               = scopeFactory;
        _logger                     = logger;
        _coordinator                = coordinator;
        _distributedLock            = distributedLock;
        _stateBuilder               = stateBuilder;
        _terminalResultStore        = terminalResultStore;
        _lifecycleTransitionService = lifecycleTransitionService;
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
                await ProcessActiveTestsAsync(readDb, writeDb, writeContext, stoppingToken);
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
        CancellationToken ct)
    {
        int minTrades    = await GetConfigAsync<int>(readDb, CK_MinTradesPerArm, DefaultMinTradesPerArm, ct);
        int maxDuration  = await GetConfigAsync<int>(readDb, CK_MaxDurationDays, DefaultMaxDurationDays, ct);
        var evaluationOptions = await GetEvaluationOptionsAsync(readDb, ct);

        // Load all active A/B tests
        var activeTests = await readDb.Set<MLSignalAbTest>()
            .AsNoTracking()
            .Where(c => c.Status == MLSignalAbTestStatus.Active && !c.IsDeleted)
            .ToListAsync(ct);

        if (activeTests.Count == 0)
        {
            _logger.LogDebug("No active A/B tests to evaluate.");
            return;
        }

        _logger.LogDebug("Evaluating {Count} active A/B test(s).", activeTests.Count);

        foreach (var test in activeTests)
        {
            try
            {
                await ProcessSingleTestAsync(
                    test, readDb, writeDb, writeContext, minTrades, maxDuration, evaluationOptions, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing A/B test {Id}.", test.Id);
            }
        }
    }

    private async Task ProcessSingleTestAsync(
        MLSignalAbTest test,
        DbContext readDb,
        DbContext writeDb,
        IWriteApplicationDbContext writeContext,
        int minTrades,
        int maxDuration,
        AbTestEvaluationOptions evaluationOptions,
        CancellationToken ct)
    {
        var championId = test.ChampionModelId;
        var challengerId = test.ChallengerModelId;

        var lockKey = $"ml:signal-abtest:{championId}:{challengerId}";
        await using var abTestLock = await _distributedLock.TryAcquireAsync(
            lockKey,
            TimeSpan.FromSeconds(5),
            ct);
        if (abTestLock is null)
        {
            _logger.LogInformation(
                "A/B test {Id} is already being processed by another worker instance. Skipping this cycle.",
                test.Id);
            return;
        }

        var symbol = test.Symbol;
        var timeframe = test.Timeframe;
        var startedAt = test.StartedAtUtc;

        // ── Guard: check if both models are still suitable for an active test ──
        var championModel = await readDb.Set<MLModel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == championId && !m.IsDeleted, ct);
        var challengerModel = await readDb.Set<MLModel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == challengerId && !m.IsDeleted, ct);

        if (championModel is null || championModel.IsSuppressed ||
            championModel.Status == MLModelStatus.Failed)
        {
            _logger.LogWarning(
                "Champion model {ChampionId} is unavailable. Ending A/B test {Symbol}/{Timeframe} without changing model states.",
                championId, symbol, timeframe);

            await InvalidateTestAsync(
                test,
                writeDb,
                writeContext,
                "Champion model was unavailable, suppressed, deleted, or failed. Test invalidated without model state changes.",
                ct);
            return;
        }

        if (challengerModel is null || challengerModel.IsSuppressed ||
            challengerModel.Status == MLModelStatus.Failed)
        {
            _logger.LogInformation(
                "Challenger model {ChallengerId} is retired/suppressed/deleted. " +
                "Auto-ending A/B test with KeepChampion for {Symbol}/{Timeframe}.",
                challengerId, symbol, timeframe);

            await InvalidateTestAsync(
                test,
                writeDb,
                writeContext,
                "Challenger model was unavailable, suppressed, deleted, or failed. Test invalidated with champion retained.",
                ct);
            return;
        }

        // ── Load resolved trade outcomes per arm ────────────────────────────
        var state = await _stateBuilder.BuildAsync(
            readDb, championId, challengerId, symbol, timeframe, startedAt, ct);

        // ── Evaluate SPRT ───────────────────────────────────────────────────
        var result = _coordinator.Evaluate(state, minTrades, maxDuration, evaluationOptions);

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
                await using (var tx = await writeDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
                {
                    await _terminalResultStore.PersistAsync(writeDb, state, result, ct);
                    await _lifecycleTransitionService.PromoteChallengerAsync(
                        writeDb, championId, challengerId, symbol, timeframe, ct);
                    await _coordinator.EndAbTestAsync(championId, challengerId, symbol, timeframe, writeContext, ct);
                    await tx.CommitAsync(ct);
                }
                break;

            case AbTestDecision.KeepChampion:
                await using (var tx = await writeDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
                {
                    await _terminalResultStore.PersistAsync(writeDb, state, result, ct);
                    await _lifecycleTransitionService.RejectChallengerAsync(writeDb, challengerId, ct);
                    await _coordinator.EndAbTestAsync(championId, challengerId, symbol, timeframe, writeContext, ct);
                    await tx.CommitAsync(ct);
                }
                break;

            case AbTestDecision.Inconclusive:
                // Test continues — no action needed
                break;
        }
    }

    private async Task InvalidateTestAsync(
        MLSignalAbTest test,
        DbContext writeDb,
        IWriteApplicationDbContext writeContext,
        string reason,
        CancellationToken ct)
    {
        var state = new AbTestState(
            test.Id,
            test.ChampionModelId,
            test.ChallengerModelId,
            test.Symbol,
            test.Timeframe,
            test.StartedAtUtc,
            [],
            []);

        var result = new AbTestResult
        {
            Decision = AbTestDecision.Invalidated,
            Reason = reason,
        };

        await using var tx = await writeDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await _terminalResultStore.PersistAsync(writeDb, state, result, ct);
        await _coordinator.EndAbTestAsync(
            test.ChampionModelId,
            test.ChallengerModelId,
            test.Symbol,
            test.Timeframe,
            writeContext,
            ct);
        await tx.CommitAsync(ct);
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

    private static async Task<AbTestEvaluationOptions> GetEvaluationOptionsAsync(
        DbContext ctx,
        CancellationToken ct)
    {
        var alpha = await GetConfigAsync<double>(ctx, CK_Alpha, DefaultAlpha, ct);
        var beta = await GetConfigAsync<double>(ctx, CK_Beta, DefaultBeta, ct);
        var deltaWinMultiplier = await GetConfigAsync<double>(
            ctx, CK_DeltaWinMultiplier, DefaultDeltaWinMultiplier, ct);
        var minimumEffectPnl = await GetConfigAsync<double>(
            ctx, CK_MinimumEffectPnl, DefaultMinimumEffectPnl, ct);
        var winsorizationQuantile = await GetConfigAsync<double>(
            ctx, CK_WinsorizationQuantile, DefaultWinsorizationQuantile, ct);
        var maxCovariateImbalance = await GetConfigAsync<double>(
            ctx, CK_MaxCovariateImbalance, DefaultMaxCovariateImbalance, ct);
        var imbalanceEvidenceMultiplier = await GetConfigAsync<double>(
            ctx, CK_ImbalanceEvidenceMultiplier, DefaultImbalanceEvidenceMultiplier, ct);

        return new AbTestEvaluationOptions(
            alpha,
            beta,
            deltaWinMultiplier,
            minimumEffectPnl > 0 ? minimumEffectPnl : null,
            winsorizationQuantile,
            maxCovariateImbalance,
            imbalanceEvidenceMultiplier);
    }
}
