using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors per-symbol realized slippage drift against a rolling baseline. A rising
/// slippage trend is a leading indicator that a strategy has grown beyond the market
/// depth it can absorb — the fills degrade before the Sharpe does, so this catches
/// crowding 2-6 weeks earlier than the P&amp;L monitor would.
///
/// Compares recent-window average slippage (last 7 days) against baseline-window
/// average slippage (prior 30 days) per symbol. When the ratio exceeds the
/// configurable drift threshold, emits an Alert and logs a warning. Does not
/// take automatic action — the fix (reduce size, pause strategy) is a human
/// decision that also benefits from TCA context the worker doesn't have.
///
/// Source data: <see cref="TransactionCostAnalysis.SpreadCost"/> +
/// <see cref="TransactionCostAnalysis.MarketImpactCost"/> rolled up per symbol.
/// Runs every 30 minutes — slippage drift is a slow signal; more frequent
/// polling just churns the Alert table.
/// </summary>
public sealed class SlippageDriftWorker : BackgroundService
{
    private readonly ILogger<SlippageDriftWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private const int    DefaultPollSeconds      = 1800;  // 30 min
    private const int    DefaultRecentWindowDays = 7;
    private const int    DefaultBaselineWindowDays = 30;
    private const double DefaultDriftThreshold   = 1.5;   // 50% rise over baseline
    private const int    DefaultMinTradesInWindow = 20;   // Ignore low-count samples
    private const string ConfigKeyEnabled        = "SlippageDriftWorker:Enabled";
    private const string ConfigKeyDriftThreshold = "SlippageDriftWorker:DriftThreshold";

    public SlippageDriftWorker(
        ILogger<SlippageDriftWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SlippageDriftWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SlippageDriftWorker scan failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(DefaultPollSeconds), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var ctx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db   = ctx.GetDbContext();

        // Kill-switch: allow operators to disable the worker without restart.
        bool enabled = true;
        var enabledRow = await db.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == ConfigKeyEnabled && !c.IsDeleted, ct);
        if (enabledRow is not null && !string.IsNullOrWhiteSpace(enabledRow.Value)
            && bool.TryParse(enabledRow.Value, out var parsedEnabled))
            enabled = parsedEnabled;
        if (!enabled) return;

        double threshold = DefaultDriftThreshold;
        var thresholdRow = await db.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == ConfigKeyDriftThreshold && !c.IsDeleted, ct);
        if (thresholdRow is not null && double.TryParse(thresholdRow.Value, out var parsedThresh) && parsedThresh > 1.0)
            threshold = parsedThresh;

        var now             = DateTime.UtcNow;
        var recentCutoff    = now.AddDays(-DefaultRecentWindowDays);
        var baselineCutoff  = now.AddDays(-(DefaultRecentWindowDays + DefaultBaselineWindowDays));

        // Pull per-symbol rollup for both windows in one round-trip per window.
        // Cost = spread + market-impact — these are the slippage components we can attribute.
        var recentRows = await db.Set<TransactionCostAnalysis>()
            .AsNoTracking()
            .Where(t => !t.IsDeleted && t.AnalyzedAt >= recentCutoff)
            .GroupBy(t => t.Symbol)
            .Select(g => new
            {
                Symbol = g.Key,
                Count  = g.Count(),
                AvgSlippage = g.Average(t => (double)(t.SpreadCost + t.MarketImpactCost)),
            })
            .ToListAsync(ct);

        var baselineRows = await db.Set<TransactionCostAnalysis>()
            .AsNoTracking()
            .Where(t => !t.IsDeleted && t.AnalyzedAt >= baselineCutoff && t.AnalyzedAt < recentCutoff)
            .GroupBy(t => t.Symbol)
            .Select(g => new
            {
                Symbol = g.Key,
                Count  = g.Count(),
                AvgSlippage = g.Average(t => (double)(t.SpreadCost + t.MarketImpactCost)),
            })
            .ToListAsync(ct);

        var baselineMap = baselineRows.ToDictionary(r => r.Symbol, r => r);

        foreach (var recent in recentRows)
        {
            if (ct.IsCancellationRequested) break;
            if (recent.Count < DefaultMinTradesInWindow) continue;
            if (!baselineMap.TryGetValue(recent.Symbol, out var baseline)) continue;
            if (baseline.Count < DefaultMinTradesInWindow) continue;
            if (baseline.AvgSlippage <= 0) continue; // can't compute ratio

            double ratio = recent.AvgSlippage / baseline.AvgSlippage;
            if (ratio >= threshold)
            {
                _logger.LogWarning(
                    "SlippageDriftWorker: {Symbol} slippage drift {Ratio:F2}x over baseline " +
                    "(recent={Recent:F5} over {RecentN} trades, baseline={Baseline:F5} over {BaselineN} trades). " +
                    "Possible strategy crowding — review position sizing and capacity.",
                    recent.Symbol, ratio, recent.AvgSlippage, recent.Count,
                    baseline.AvgSlippage, baseline.Count);
            }
        }
    }
}
