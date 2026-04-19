using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Resolves signal conflicts per (symbol, timeframe) using a priority scoring system.
/// <list type="bullet">
///   <item>Groups pending signals by (symbol, timeframe) — an H1 scalp and a D1 trend on
///         the same symbol operate at different horizons and are not in conflict.</item>
///   <item>Within each group, if signals conflict (opposing directions), suppresses all for that group.</item>
///   <item>Within each group of same-direction signals, keeps only the highest-scoring signal.</item>
///   <item>Score = ML confidence (40%) + strategy Sharpe (30%) + capacity headroom (30%).</item>
/// </list>
/// Note: regime coherence filtering is handled upstream in StrategyWorker (via
/// RegimeCoherenceChecker) before signals reach this resolver. This class only
/// resolves directional conflicts and deduplicates same-direction signals.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class SignalConflictResolver : ISignalConflictResolver
{
    private readonly ILogger<SignalConflictResolver> _logger;

    private const decimal MlConfidenceWeight = 0.40m;
    private const decimal SharpeWeight       = 0.30m;
    private const decimal CapacityWeight     = 0.30m;

    public SignalConflictResolver(ILogger<SignalConflictResolver> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<PendingSignal> Resolve(IReadOnlyList<PendingSignal> pendingSignals)
    {
        if (pendingSignals.Count <= 1)
            return pendingSignals;

        var winners = new List<PendingSignal>();

        // Group by (symbol, timeframe). Same-symbol signals on different timeframes
        // are not in conflict — a BUY EURUSD on H1 (scalp) can coexist with a SELL
        // EURUSD on D1 (trend). Collapsing them would force one strategy family to
        // defer to another despite operating at independent horizons.
        var byGroup = pendingSignals.GroupBy(s => (s.Symbol, s.Timeframe));

        foreach (var group in byGroup)
        {
            var (symbol, timeframe) = group.Key;
            var signals = group.ToList();

            if (signals.Count == 1)
            {
                winners.Add(signals[0]);
                continue;
            }

            // Check for opposing directions — suppress entire (symbol, timeframe) group
            var directions = signals.Select(s => s.Direction).Distinct().ToList();
            if (directions.Count > 1)
            {
                _logger.LogInformation(
                    "SignalConflict: opposing directions for {Symbol}/{Timeframe} ({Count} signals) — suppressing all",
                    symbol, timeframe, signals.Count);
                continue;
            }

            // Same direction — keep highest scoring signal
            var scored = signals
                .Select(s => (Signal: s, Score: ComputeScore(s)))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Signal.ExpiresAt)   // Earlier expiry = more urgent
                .ThenBy(x => x.Signal.StrategyId) // Final deterministic tie-break
                .ToList();

            var winner = scored[0];
            winners.Add(winner.Signal);

            if (scored.Count > 1)
            {
                _logger.LogInformation(
                    "SignalConflict: {Count} same-direction signals for {Symbol}/{Timeframe} {Dir} — winner strategy {WinnerId} (score={Score:F4}), suppressed {Suppressed}",
                    signals.Count, symbol, timeframe, directions[0],
                    winner.Signal.StrategyId, winner.Score,
                    string.Join(",", scored.Skip(1).Select(s => s.Signal.StrategyId)));
            }
        }

        return winners;
    }

    private static decimal ComputeScore(PendingSignal signal)
    {
        // ML confidence: 0–1, higher is better
        decimal mlScore = signal.MLConfidenceScore ?? signal.Confidence;

        // Strategy Sharpe: normalize to 0–1 range (assume Sharpe 0–3 is typical range).
        // When unknown, use a small penalty (0.25) instead of neutral (0.5) so that
        // strategies with known Sharpe are preferred. Break remaining ties by strategy ID
        // for deterministic ordering.
        decimal sharpeNormalized = signal.StrategySharpeRatio.HasValue
            ? Math.Clamp(signal.StrategySharpeRatio.Value / 3.0m, 0m, 1.0m)
            : 0.25m;

        // Capacity headroom: ratio of available capacity (higher = more room)
        decimal capacityScore = signal.EstimatedCapacityLots.HasValue && signal.EstimatedCapacityLots.Value > 0
            ? Math.Clamp(1.0m - signal.SuggestedLotSize / signal.EstimatedCapacityLots.Value, 0m, 1.0m)
            : 0.5m; // Neutral if unknown

        return mlScore * MlConfidenceWeight
             + sharpeNormalized * SharpeWeight
             + capacityScore * CapacityWeight;
    }
}
