using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Applies stress test scenarios to the current portfolio, computing P&amp;L impact,
/// margin call risk, and per-position attribution. Supports historical replay,
/// hypothetical shocks, and reverse stress testing.
/// </summary>
[RegisterService]
public class StressTestEngine : IStressTestEngine
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly IPortfolioRiskCalculator _riskCalculator;
    private readonly ILogger<StressTestEngine> _logger;

    public StressTestEngine(
        IReadApplicationDbContext readContext,
        IPortfolioRiskCalculator riskCalculator,
        ILogger<StressTestEngine> logger)
    {
        _readContext     = readContext;
        _riskCalculator = riskCalculator;
        _logger          = logger;
    }

    public async Task<StressTestResult> RunScenarioAsync(
        StressTestScenario scenario,
        TradingAccount account,
        IReadOnlyList<Position> openPositions,
        CancellationToken cancellationToken)
    {
        return scenario.ScenarioType switch
        {
            StressScenarioType.Historical   => await RunHistoricalAsync(scenario, account, openPositions, cancellationToken),
            StressScenarioType.Hypothetical => RunHypothetical(scenario, account, openPositions),
            StressScenarioType.ReverseStress => await RunReverseStressAsync(scenario, account, openPositions, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario.ScenarioType))
        };
    }

    private async Task<StressTestResult> RunHistoricalAsync(
        StressTestScenario scenario,
        TradingAccount account,
        IReadOnlyList<Position> positions,
        CancellationToken ct)
    {
        var def = JsonSerializer.Deserialize<HistoricalShockDef>(scenario.ShockDefinitionJson)
                  ?? throw new InvalidOperationException("Invalid historical shock definition");

        // Compute per-symbol returns during the historical event window
        var impacts = new List<PositionImpact>();
        decimal totalPnl = 0;

        foreach (var position in positions)
        {
            var candles = await _readContext.GetDbContext()
                .Set<Candle>()
                .Where(c => c.Symbol == position.Symbol && c.Timeframe == Timeframe.D1
                         && c.IsClosed && !c.IsDeleted
                         && c.Timestamp >= def.DateFrom && c.Timestamp <= def.DateTo)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            if (candles.Count < 2) continue;

            var startPrice = candles.First().Open;
            var endPrice   = candles.Last().Close;
            var returnPct  = startPrice != 0 ? (endPrice - startPrice) / startPrice : 0;

            decimal direction = position.Direction == PositionDirection.Long ? 1m : -1m;
            decimal pnlImpact = position.OpenLots * position.AverageEntryPrice * direction * returnPct;

            totalPnl += pnlImpact;
            impacts.Add(new PositionImpact(position.Id, position.Symbol, pnlImpact));
        }

        return BuildResult(scenario, account, positions, totalPnl, impacts);
    }

    private StressTestResult RunHypothetical(
        StressTestScenario scenario,
        TradingAccount account,
        IReadOnlyList<Position> positions)
    {
        var def = JsonSerializer.Deserialize<HypotheticalShockDef>(scenario.ShockDefinitionJson)
                  ?? throw new InvalidOperationException("Invalid hypothetical shock definition");

        var shockMap = def.Shocks.ToDictionary(s => s.Symbol, s => s.PctChange);

        var impacts = new List<PositionImpact>();
        decimal totalPnl = 0;

        foreach (var position in positions)
        {
            if (!shockMap.TryGetValue(position.Symbol, out var pctChange)) continue;

            decimal direction = position.Direction == PositionDirection.Long ? 1m : -1m;
            decimal pnlImpact = position.OpenLots * position.AverageEntryPrice * direction * (pctChange / 100m);

            totalPnl += pnlImpact;
            impacts.Add(new PositionImpact(position.Id, position.Symbol, pnlImpact));
        }

        return BuildResult(scenario, account, positions, totalPnl, impacts);
    }

    private async Task<StressTestResult> RunReverseStressAsync(
        StressTestScenario scenario,
        TradingAccount account,
        IReadOnlyList<Position> positions,
        CancellationToken ct)
    {
        var def = JsonSerializer.Deserialize<ReverseStressDef>(scenario.ShockDefinitionJson)
                  ?? throw new InvalidOperationException("Invalid reverse stress definition");

        // Binary search for the minimum uniform shock that causes the target loss
        decimal targetLoss = account.Equity * (def.TargetLossPct / 100m);
        decimal low = 0m, high = 50m; // Search 0% to 50% shock
        decimal minShock = high;

        for (int iter = 0; iter < 50; iter++)
        {
            decimal mid = (low + high) / 2m;
            decimal estLoss = 0;

            foreach (var pos in positions)
            {
                // Worst-case assumes adverse move for every position:
                // longs lose on a drop, shorts lose on a rally. Use gross notional.
                estLoss += pos.OpenLots * pos.AverageEntryPrice * (mid / 100m);
            }

            if (estLoss >= targetLoss)
            {
                minShock = mid;
                high = mid;
            }
            else
            {
                low = mid;
            }

            if (high - low < 0.01m) break;
        }

        var result = BuildResult(scenario, account, positions, -targetLoss, new List<PositionImpact>());
        result.MinimumShockPct = minShock;
        return result;
    }

    private StressTestResult BuildResult(
        StressTestScenario scenario,
        TradingAccount account,
        IReadOnlyList<Position> positions,
        decimal stressedPnl,
        List<PositionImpact> impacts)
    {
        var stressedPnlPct = account.Equity > 0 ? stressedPnl / account.Equity * 100m : 0;
        var postStressEquity = account.Equity + stressedPnl;

        // Would this trigger a margin call? (equity below used margin)
        var totalMargin = positions.Sum(p => p.OpenLots * p.AverageEntryPrice / 100m); // Simplified leverage=100
        var wouldTriggerMarginCall = postStressEquity < totalMargin;

        return new StressTestResult
        {
            StressTestScenarioId  = scenario.Id,
            TradingAccountId      = account.Id,
            PortfolioEquity       = account.Equity,
            StressedPnl           = stressedPnl,
            StressedPnlPct        = stressedPnlPct,
            WouldTriggerMarginCall = wouldTriggerMarginCall,
            PositionImpactsJson   = JsonSerializer.Serialize(impacts),
            ExecutedAt            = DateTime.UtcNow
        };
    }

    // ── DTOs for shock definition parsing ──

    private record HistoricalShockDef(DateTime DateFrom, DateTime DateTo);
    private record HypotheticalShockDef(List<SymbolShock> Shocks, decimal SpreadMultiplier = 1);
    private record SymbolShock(string Symbol, decimal PctChange);
    private record ReverseStressDef(decimal TargetLossPct, List<string> SearchSymbols);
    private record PositionImpact(long PositionId, string Symbol, decimal PnlImpact);
}
