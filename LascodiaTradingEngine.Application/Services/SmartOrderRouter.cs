using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Selects optimal execution venue by ranking active EA instances and eligible accounts
/// using heartbeat freshness, account equity, and recent execution quality metrics
/// (slippage and fill latency from ExecutionQualityLog).
/// Also determines the optimal execution algorithm based on order size relative to
/// average daily volume.
/// </summary>
[RegisterService]
public class SmartOrderRouter : ISmartOrderRouter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SmartOrderRouter> _logger;

    // Weighting factors for venue selection scoring
    private const decimal HealthWeight           = 0.35m;
    private const decimal MarginWeight           = 0.30m;
    private const decimal ExecutionQualityWeight = 0.35m;

    /// <summary>Lookback period for execution quality metrics.</summary>
    private const int ExecQualityLookbackDays = 7;

    /// <summary>Number of recent fills to use for spread/slippage estimates.</summary>
    private const int CostEstimateLookbackCount = 50;

    /// <summary>Order size threshold (fraction of ADV) below which Direct execution is used.</summary>
    private const decimal DirectThreshold = 0.01m;

    /// <summary>Order size threshold (fraction of ADV) above which VWAP is used instead of TWAP.</summary>
    private const decimal VwapThreshold = 0.05m;

    public SmartOrderRouter(
        IServiceScopeFactory scopeFactory,
        ILogger<SmartOrderRouter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task<OrderRoutingDecision> RouteAsync(
        TradeSignal signal,
        IReadOnlyList<EAInstance> activeInstances,
        IReadOnlyList<TradingAccount> eligibleAccounts,
        CancellationToken cancellationToken)
    {
        if (activeInstances.Count == 0)
            throw new InvalidOperationException($"No active EA instances available for {signal.Symbol}");

        if (eligibleAccounts.Count == 0)
            throw new InvalidOperationException($"No eligible accounts available for {signal.Symbol}");

        // Query recent execution quality metrics per account
        var execQualityByAccount = await LoadExecutionQualityAsync(
            eligibleAccounts.Select(a => a.Id).ToList(),
            cancellationToken);

        // Score each (instance, account) combination
        var bestScore   = decimal.MinValue;
        string bestInstance  = activeInstances[0].InstanceId;
        long   bestAccount   = eligibleAccounts[0].Id;
        string  bestReason   = string.Empty;

        var now = DateTime.UtcNow;
        var maxEquity = eligibleAccounts.Max(a => a.Equity);

        foreach (var instance in activeInstances)
        {
            foreach (var account in eligibleAccounts)
            {
                // Health score: based on heartbeat freshness (fresher = higher score)
                var secondsSinceHeartbeat = (decimal)(now - instance.LastHeartbeat).TotalSeconds;
                var healthScore = Math.Max(0m, 1.0m - secondsSinceHeartbeat / 60m); // 0 at 60s stale

                // Margin score: higher equity = better capacity
                var marginScore = maxEquity > 0
                    ? account.Equity / maxEquity
                    : 0m;

                // Execution quality score: lower slippage and latency = higher score
                var execScore = ComputeExecutionQualityScore(
                    account.Id, execQualityByAccount);

                var totalScore = healthScore * HealthWeight
                               + marginScore * MarginWeight
                               + execScore  * ExecutionQualityWeight;

                if (totalScore > bestScore)
                {
                    bestScore    = totalScore;
                    bestInstance = instance.InstanceId;
                    bestAccount  = account.Id;
                    bestReason   = $"Score={totalScore:F4} (health={healthScore:F2}, margin={marginScore:F2}, execQuality={execScore:F2})";
                }
            }
        }

        // Compute expected execution costs from recent fills for this symbol
        var (expectedSpread, expectedSlippage) = await EstimateExecutionCostsAsync(
            signal.Symbol, cancellationToken);

        // Determine optimal execution algorithm based on order size vs average daily volume
        var algorithm = await DetermineAlgorithmAsync(
            signal.Symbol, signal.SuggestedLotSize, cancellationToken);

        _logger.LogInformation(
            "SOR: routed {Symbol} signal to instance={Instance}, account={Account}, algo={Algorithm}. {Reason}",
            signal.Symbol, bestInstance, bestAccount, algorithm, bestReason);

        return new OrderRoutingDecision(
            SelectedInstanceId: bestInstance,
            SelectedAccountId: bestAccount,
            ExpectedSpread: expectedSpread,
            ExpectedSlippagePips: expectedSlippage,
            RoutingReason: bestReason,
            ExpectedAlgorithm: algorithm);
    }

    /// <summary>
    /// Loads recent execution quality metrics (average slippage and fill latency)
    /// grouped by the account that owns each order.
    /// </summary>
    private async Task<Dictionary<long, (decimal AvgSlippage, decimal AvgLatencyMs, int Count)>>
        LoadExecutionQualityAsync(List<long> accountIds, CancellationToken ct)
    {
        var result = new Dictionary<long, (decimal AvgSlippage, decimal AvgLatencyMs, int Count)>();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var cutoff = DateTime.UtcNow.AddDays(-ExecQualityLookbackDays);

            // Query execution quality logs joined to orders to get account context.
            // ExecutionQualityLog has OrderId -> Order has TradingAccountId.
            var metrics = await readContext.GetDbContext()
                .Set<ExecutionQualityLog>()
                .Where(e => !e.IsDeleted && e.RecordedAt >= cutoff)
                .Join(
                    readContext.GetDbContext().Set<Order>().Where(o => accountIds.Contains(o.TradingAccountId)),
                    eq => eq.OrderId,
                    o => o.Id,
                    (eq, o) => new { o.TradingAccountId, eq.SlippagePips, eq.SubmitToFillMs })
                .GroupBy(x => x.TradingAccountId)
                .Select(g => new
                {
                    AccountId = g.Key,
                    AvgSlippage = g.Average(x => Math.Abs(x.SlippagePips)),
                    AvgLatencyMs = g.Average(x => (decimal)x.SubmitToFillMs),
                    Count = g.Count()
                })
                .ToListAsync(ct);

            foreach (var m in metrics)
                result[m.AccountId] = (m.AvgSlippage, m.AvgLatencyMs, m.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SOR: failed to load execution quality metrics, using neutral scores");
        }

        return result;
    }

    /// <summary>
    /// Computes a 0-1 execution quality score for an account.
    /// Lower slippage and lower latency produce a higher score.
    /// Accounts with no recent data receive a neutral 0.5 score.
    /// </summary>
    private static decimal ComputeExecutionQualityScore(
        long accountId,
        Dictionary<long, (decimal AvgSlippage, decimal AvgLatencyMs, int Count)> metrics)
    {
        if (!metrics.TryGetValue(accountId, out var m) || m.Count == 0)
            return 0.5m; // Neutral score when no data

        // Slippage component: 0 pips = 1.0, 5+ pips = 0.0 (linear)
        var slippageScore = Math.Max(0m, 1.0m - m.AvgSlippage / 5.0m);

        // Latency component: 0 ms = 1.0, 2000+ ms = 0.0 (linear)
        var latencyScore = Math.Max(0m, 1.0m - m.AvgLatencyMs / 2000m);

        // Blend 60% slippage, 40% latency
        return slippageScore * 0.6m + latencyScore * 0.4m;
    }

    /// <summary>
    /// Estimates expected spread and slippage for a symbol based on the last
    /// <see cref="CostEstimateLookbackCount"/> fills from ExecutionQualityLog.
    /// Falls back to 0 if no execution history is available.
    /// </summary>
    private async Task<(decimal ExpectedSpread, decimal ExpectedSlippagePips)> EstimateExecutionCostsAsync(
        string symbol, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

            // Get average spread from SpreadProfile (most recent median spread for this symbol)
            var latestSpreadProfile = await readContext.GetDbContext()
                .Set<SpreadProfile>()
                .Where(sp => sp.Symbol == symbol && !sp.IsDeleted)
                .OrderByDescending(sp => sp.ComputedAt)
                .Select(sp => sp.SpreadMean)
                .FirstOrDefaultAsync(ct);

            // Get average slippage from recent execution quality logs
            var recentSlippage = await readContext.GetDbContext()
                .Set<ExecutionQualityLog>()
                .Where(e => e.Symbol == symbol && !e.IsDeleted)
                .OrderByDescending(e => e.RecordedAt)
                .Take(CostEstimateLookbackCount)
                .Select(e => Math.Abs(e.SlippagePips))
                .ToListAsync(ct);

            decimal expectedSpread = latestSpreadProfile;
            decimal expectedSlippage = recentSlippage.Count > 0
                ? recentSlippage.Average()
                : 0m;

            return (expectedSpread, expectedSlippage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SOR: failed to estimate execution costs for {Symbol}, defaulting to 0", symbol);
            return (0m, 0m);
        }
    }

    /// <summary>
    /// Determines the optimal execution algorithm based on order size relative to
    /// average daily volume (ADV) estimated from recent tick data.
    /// - Order size &lt; 1% of ADV: Direct (no slicing needed)
    /// - Order size 1%-5% of ADV: TWAP (reduce timing risk)
    /// - Order size &gt;= 5% of ADV: VWAP (minimize market impact)
    /// </summary>
    private async Task<ExecutionAlgorithmType> DetermineAlgorithmAsync(
        string symbol, decimal orderSize, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

            // Estimate average daily volume from tick data over the last 7 days
            var cutoff = DateTime.UtcNow.AddDays(-7);
            var volumeStats = await readContext.GetDbContext()
                .Set<TickRecord>()
                .Where(t => t.Symbol == symbol && t.TickTimestamp >= cutoff && !t.IsDeleted)
                .GroupBy(t => t.TickTimestamp.Date)
                .Select(g => g.Sum(t => t.TickVolume))
                .ToListAsync(ct);

            if (volumeStats.Count == 0)
            {
                // No volume data available — default to TWAP for safety
                _logger.LogDebug("SOR: no volume data for {Symbol}, defaulting to TWAP", symbol);
                return ExecutionAlgorithmType.TWAP;
            }

            decimal avgDailyVolume = volumeStats.Average(v => (decimal)v);

            if (avgDailyVolume <= 0)
                return ExecutionAlgorithmType.TWAP;

            decimal sizeRatio = orderSize / avgDailyVolume;

            if (sizeRatio < DirectThreshold)
                return ExecutionAlgorithmType.Direct;
            else if (sizeRatio < VwapThreshold)
                return ExecutionAlgorithmType.TWAP;
            else
                return ExecutionAlgorithmType.VWAP;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SOR: failed to determine algorithm for {Symbol}, defaulting to TWAP", symbol);
            return ExecutionAlgorithmType.TWAP;
        }
    }
}
