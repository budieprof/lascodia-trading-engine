using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Manages gradual parameter rollout after optimization approval. New parameters start
/// at 25% traffic, ramp to 100% after a configurable observation window, and automatically
/// roll back if post-rollout performance degrades below the approval threshold.
/// </summary>
internal sealed class GradualRolloutManager
{
    private readonly ILogger _logger;

    internal GradualRolloutManager(ILogger logger) => _logger = logger;

    /// <summary>
    /// Initiates a gradual rollout: saves the current params as rollback, sets the new params,
    /// and starts at <paramref name="initialPct"/>% traffic.
    /// </summary>
    internal static void StartRollout(
        Strategy strategy, string newParamsJson, long optimizationRunId, int initialPct = 25)
    {
        strategy.RollbackParametersJson = strategy.ParametersJson;
        strategy.ParametersJson = CanonicalParameterJson.Normalize(newParamsJson);
        strategy.RolloutPct = initialPct;
        strategy.RolloutStartedAt = DateTime.UtcNow;
        strategy.RolloutOptimizationRunId = optimizationRunId;
    }

    /// <summary>
    /// Promotes the rollout to the next tier (25→50→75→100) or completes it.
    /// Returns true if the rollout is now complete (100%).
    /// </summary>
    internal static bool PromoteRollout(Strategy strategy)
    {
        int current = strategy.RolloutPct ?? 100;
        int next = current switch
        {
            <= 25 => 50,
            <= 50 => 75,
            <= 75 => 100,
            _     => 100,
        };

        strategy.RolloutPct = next;

        if (next >= 100)
        {
            // Rollout complete — clear rollback state
            strategy.RollbackParametersJson = null;
            strategy.RolloutStartedAt = null;
            strategy.RolloutOptimizationRunId = null;
            strategy.RolloutPct = null;
            return true;
        }

        // Reset the observation window at each intermediate tier so 50%/75%
        // promotions require fresh evidence from that tier instead of inheriting
        // time and snapshots collected while the strategy was still at 25%/50%.
        strategy.RolloutStartedAt = DateTime.UtcNow;

        return false;
    }

    /// <summary>
    /// Rolls back to the pre-optimization parameters and clears rollout state.
    /// </summary>
    internal static void Rollback(Strategy strategy)
    {
        if (!string.IsNullOrWhiteSpace(strategy.RollbackParametersJson))
            strategy.ParametersJson = strategy.RollbackParametersJson;

        strategy.RollbackParametersJson = null;
        strategy.RolloutPct = null;
        strategy.RolloutStartedAt = null;
        strategy.RolloutOptimizationRunId = null;
    }

    /// <summary>
    /// Checks if a strategy in active rollout should be promoted or rolled back based on
    /// recent performance snapshots. Called periodically by the StrategyHealthWorker or
    /// OptimizationWorker's auto-scheduling scan.
    /// </summary>
    /// <returns>
    /// "promoted" if promoted to next tier, "completed" if rollout finished,
    /// "rolledback" if performance degraded, or null if no action needed.
    /// </returns>
    internal async Task<string?> EvaluateRolloutAsync(
        Strategy strategy, DbContext db, decimal minHealthScore,
        int observationWindowDays, CancellationToken ct)
    {
        if (strategy.RolloutPct is null or >= 100) return null;
        if (strategy.RolloutStartedAt is null) return null;

        // Check if we've been in this rollout tier long enough
        double daysSinceStart = (DateTime.UtcNow - strategy.RolloutStartedAt.Value).TotalDays;
        double daysPerTier = observationWindowDays / 4.0; // 4 tiers: 25→50→75→100
        if (daysSinceStart < daysPerTier) return null;

        // Load recent performance snapshots since rollout started
        var recentSnapshots = await db.Set<StrategyPerformanceSnapshot>()
            .Where(s => s.StrategyId == strategy.Id
                     && s.EvaluatedAt >= strategy.RolloutStartedAt.Value
                     && !s.IsDeleted)
            .OrderByDescending(s => s.EvaluatedAt)
            .Take(5)
            .Select(s => s.HealthScore)
            .ToListAsync(ct);

        if (recentSnapshots.Count < 2) return null; // Not enough data yet

        decimal avgScore = recentSnapshots.Average(s => s);

        // Rollback if average score during rollout is below approval threshold
        if (avgScore < minHealthScore)
        {
            _logger.LogWarning(
                "GradualRolloutManager: rolling back strategy {Id} — avg score {Score:F2} < {Threshold:F2} during rollout",
                strategy.Id, avgScore, minHealthScore);
            Rollback(strategy);
            return "rolledback";
        }

        // Check for deterioration trend using weighted linear regression across all
        // available snapshots rather than only the 3 most recent. This prevents a
        // single positive spike from masking a sustained downtrend. Snapshots are
        // ordered newest-first, so a negative slope means scores are declining over time.
        if (recentSnapshots.Count >= 3)
        {
            // Compute weighted linear regression slope (newer snapshots weighted higher).
            // x = 0 (oldest) to n-1 (newest), y = health score
            int n = recentSnapshots.Count;
            double sumW = 0, sumWx = 0, sumWy = 0, sumWxx = 0, sumWxy = 0;
            for (int i = 0; i < n; i++)
            {
                // Reverse index: recentSnapshots[0] is newest → x = n-1
                int x = n - 1 - i;
                double y = (double)recentSnapshots[i];
                double w = 1.0 + x; // Newer snapshots get higher weight
                sumW   += w;
                sumWx  += w * x;
                sumWy  += w * y;
                sumWxx += w * x * x;
                sumWxy += w * x * y;
            }
            double denom = sumW * sumWxx - sumWx * sumWx;
            if (denom != 0)
            {
                double slope = (sumW * sumWxy - sumWx * sumWy) / denom;
                // Negative slope means declining scores. Trigger rollback if the
                // predicted decline over the observation window exceeds 10% of the
                // current average, indicating a meaningful downtrend, not noise.
                double predictedDecline = Math.Abs(slope) * n;
                // Use the larger of relative threshold (10% of avg) and absolute floor (0.02)
                // to prevent oversensitive rollbacks when avgScore is very small.
                double declineThreshold = Math.Max((double)avgScore * 0.10, 0.02);
                if (slope < 0 && predictedDecline > declineThreshold)
                {
                    _logger.LogWarning(
                        "GradualRolloutManager: rolling back strategy {Id} — deterioration trend during rollout " +
                        "(slope={Slope:F4}, predicted decline={Decline:F2}, scores={Scores})",
                        strategy.Id, slope, predictedDecline,
                        string.Join("→", recentSnapshots.Select(s => s.ToString("F2"))));
                    Rollback(strategy);
                    return "rolledback";
                }
            }
        }

        // All good — promote to next tier
        bool completed = PromoteRollout(strategy);
        _logger.LogInformation(
            "GradualRolloutManager: strategy {Id} rollout {Action} — avg score {Score:F2}, now at {Pct}%",
            strategy.Id, completed ? "completed" : "promoted", avgScore, strategy.RolloutPct ?? 100);
        return completed ? "completed" : "promoted";
    }

    /// <summary>
    /// Determines which parameter set to use for a given signal generation based on
    /// the rollout percentage. Returns the new params <paramref name="rolloutPct"/>% of
    /// the time, and the rollback params otherwise.
    /// </summary>
    internal static string SelectParameters(Strategy strategy, int deterministicSeed)
    {
        if (strategy.RolloutPct is null or >= 100
            || string.IsNullOrWhiteSpace(strategy.RollbackParametersJson))
        {
            return strategy.ParametersJson;
        }

        // Deterministic selection based on seed — same seed always picks the same branch
        int bucket = Math.Abs(deterministicSeed) % 100;
        return bucket < strategy.RolloutPct.Value
            ? strategy.ParametersJson        // New params
            : strategy.RollbackParametersJson; // Rollback params
    }
}
