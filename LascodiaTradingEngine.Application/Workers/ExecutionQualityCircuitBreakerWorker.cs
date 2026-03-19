using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MediatR;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors execution quality per strategy and auto-pauses any strategy whose rolling
/// average slippage or fill latency persistently exceeds configured thresholds.
///
/// <para>
/// <see cref="OrderExecutionWorker"/> writes a <see cref="ExecutionQualityLog"/> for
/// every filled order. Without a consumer that acts on those records, a strategy can
/// silently incur 3-5 pip slippage on every trade — eroding edge that backtests never
/// modelled. This worker closes that feedback loop.
/// </para>
///
/// Decision rules (evaluated over the last <c>ExecQuality:WindowFills</c> fills):
/// <list type="bullet">
///   <item><description>
///     Avg <see cref="ExecutionQualityLog.SlippagePips"/> &gt;
///     <c>ExecQuality:MaxAvgSlippagePips</c> → strategy paused.
///   </description></item>
///   <item><description>
///     Avg <see cref="ExecutionQualityLog.SubmitToFillMs"/> &gt;
///     <c>ExecQuality:MaxAvgLatencyMs</c> → strategy paused.
///   </description></item>
/// </list>
///
/// Config keys (EngineConfig, all hot-reloadable):
/// <list type="bullet">
///   <item><description><c>ExecQuality:PollIntervalMinutes</c> — default 15</description></item>
///   <item><description><c>ExecQuality:WindowFills</c> — default 20 (minimum fills required)</description></item>
///   <item><description><c>ExecQuality:MaxAvgSlippagePips</c> — default 3.0</description></item>
///   <item><description><c>ExecQuality:MaxAvgLatencyMs</c> — default 2000</description></item>
///   <item><description><c>ExecQuality:AutoPauseEnabled</c> — default true</description></item>
/// </list>
/// </summary>
public sealed class ExecutionQualityCircuitBreakerWorker : BackgroundService
{
    private const string CK_PollMins     = "ExecQuality:PollIntervalMinutes";
    private const string CK_WindowFills  = "ExecQuality:WindowFills";
    private const string CK_MaxSlippage  = "ExecQuality:MaxAvgSlippagePips";
    private const string CK_MaxLatencyMs = "ExecQuality:MaxAvgLatencyMs";
    private const string CK_AutoPause    = "ExecQuality:AutoPauseEnabled";

    private readonly IServiceScopeFactory                              _scopeFactory;
    private readonly ILogger<ExecutionQualityCircuitBreakerWorker>     _logger;

    public ExecutionQualityCircuitBreakerWorker(
        IServiceScopeFactory                             scopeFactory,
        ILogger<ExecutionQualityCircuitBreakerWorker>    logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExecutionQualityCircuitBreakerWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollMins = 15;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var ctx      = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollMins            = await GetConfigAsync<int>   (ctx, CK_PollMins,    15,   stoppingToken);
                int    windowFills  = await GetConfigAsync<int>   (ctx, CK_WindowFills, 20,   stoppingToken);
                double maxSlippage  = await GetConfigAsync<double>(ctx, CK_MaxSlippage, 3.0,  stoppingToken);
                double maxLatencyMs = await GetConfigAsync<double>(ctx, CK_MaxLatencyMs,2000, stoppingToken);
                bool   autoPause    = await GetConfigAsync<bool>  (ctx, CK_AutoPause,   true, stoppingToken);

                await CheckAllStrategiesAsync(
                    ctx, writeCtx, mediator,
                    windowFills, maxSlippage, maxLatencyMs, autoPause,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExecutionQualityCircuitBreakerWorker loop error");
            }

            await Task.Delay(TimeSpan.FromMinutes(pollMins), stoppingToken);
        }

        _logger.LogInformation("ExecutionQualityCircuitBreakerWorker stopping.");
    }

    // ── Per-strategy quality check ────────────────────────────────────────────

    private async Task CheckAllStrategiesAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        int                                     windowFills,
        double                                  maxSlippage,
        double                                  maxLatencyMs,
        bool                                    autoPause,
        CancellationToken                       ct)
    {
        // Only evaluate strategies that have at least one execution quality log
        var strategyIds = await readCtx.Set<ExecutionQualityLog>()
            .Where(l => l.StrategyId != null && !l.IsDeleted)
            .Select(l => l.StrategyId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (strategyIds.Count == 0) return;

        _logger.LogDebug(
            "ExecutionQualityCircuitBreakerWorker: checking {Count} strategy/strategies.",
            strategyIds.Count);

        foreach (var strategyId in strategyIds)
        {
            ct.ThrowIfCancellationRequested();
            await CheckStrategyAsync(
                strategyId, readCtx, writeCtx, mediator,
                windowFills, maxSlippage, maxLatencyMs, autoPause, ct);
        }
    }

    private async Task CheckStrategyAsync(
        long                                    strategyId,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        int                                     windowFills,
        double                                  maxSlippage,
        double                                  maxLatencyMs,
        bool                                    autoPause,
        CancellationToken                       ct)
    {
        // Load the most recent N fills for this strategy
        var logs = await readCtx.Set<ExecutionQualityLog>()
            .Where(l => l.StrategyId == strategyId && !l.IsDeleted)
            .OrderByDescending(l => l.RecordedAt)
            .Take(windowFills)
            .AsNoTracking()
            .ToListAsync(ct);

        if (logs.Count < windowFills)
        {
            _logger.LogDebug(
                "ExecQualityCircuitBreaker: strategy {Id} has only {N}/{Min} fills — skipping.",
                strategyId, logs.Count, windowFills);
            return;
        }

        double avgSlippage  = (double)logs.Average(l => l.SlippagePips);
        double avgLatency   = logs.Average(l => l.SubmitToFillMs);

        bool slippageBreached = avgSlippage  > maxSlippage;
        bool latencyBreached  = avgLatency   > maxLatencyMs;

        if (!slippageBreached && !latencyBreached)
        {
            _logger.LogDebug(
                "ExecQualityCircuitBreaker: strategy {Id} OK — avgSlippage={Slip:F2} pips, avgLatency={Lat:F0} ms.",
                strategyId, avgSlippage, avgLatency);
            return;
        }

        // Build breach reason
        var reasons = new List<string>();
        if (slippageBreached)
            reasons.Add($"avgSlippage={avgSlippage:F2} pips > threshold {maxSlippage:F1} pips");
        if (latencyBreached)
            reasons.Add($"avgLatency={avgLatency:F0} ms > threshold {maxLatencyMs:F0} ms");

        string reason = string.Join("; ", reasons) + $" (over last {windowFills} fills)";

        _logger.LogWarning(
            "ExecQualityCircuitBreaker: strategy {Id} breached execution quality thresholds — {Reason}",
            strategyId, reason);

        if (autoPause)
        {
            // Only pause if currently Active
            int affected = await writeCtx.Set<Strategy>()
                .Where(s => s.Id == strategyId && s.Status == StrategyStatus.Active && !s.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, StrategyStatus.Paused),
                    ct);

            if (affected > 0)
            {
                _logger.LogWarning(
                    "ExecQualityCircuitBreaker: strategy {Id} AUTO-PAUSED due to poor execution quality.",
                    strategyId);

                await mediator.Send(new LogDecisionCommand
                {
                    EntityType   = "Strategy",
                    EntityId     = strategyId,
                    DecisionType = "ExecQualityCircuitBreak",
                    Outcome      = "Paused",
                    Reason       = reason,
                    Source       = "ExecutionQualityCircuitBreakerWorker"
                }, ct);
            }
        }
        else
        {
            // Audit-only mode — log but don't pause
            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "Strategy",
                EntityId     = strategyId,
                DecisionType = "ExecQualityWarning",
                Outcome      = "Warning",
                Reason       = reason + " (AutoPause disabled)",
                Source       = "ExecutionQualityCircuitBreakerWorker"
            }, ct);
        }
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
