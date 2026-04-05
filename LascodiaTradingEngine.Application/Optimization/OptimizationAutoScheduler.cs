using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Encapsulates the auto-scheduling logic for the optimization pipeline.
/// Identifies underperforming strategies, applies severity-prioritized scheduling,
/// manages cooldown periods, chronic failure escalation, and low-ROI deprioritization.
/// </summary>
/// <remarks>
/// This class serves as the logical boundary for scheduling concerns. The actual
/// implementation lives in <c>OptimizationWorker.AutoScheduleUnderperformersAsync</c>
/// and related methods. This facade exists for organizational clarity.
/// </remarks>
internal sealed class OptimizationAutoScheduler
{
    private readonly TradingMetrics _metrics;
    private readonly ILogger _logger;

    internal OptimizationAutoScheduler(TradingMetrics metrics, ILogger logger)
    {
        _metrics = metrics;
        _logger  = logger;
    }

    /// <summary>
    /// Computes a scheduling priority for a strategy based on its performance metrics.
    /// Priority 0 (most urgent) = fails performance gate outright.
    /// Priority 1 = passes gate but shows deterioration trend.
    /// Priority 2+ = deprioritized due to poor optimization ROI.
    /// </summary>
    internal static (int Priority, decimal Severity) ComputeSchedulingPriority(
        bool meetsGate, bool deteriorating, decimal healthScore, decimal decline, double approvalRate)
    {
        int priority;
        decimal severity;

        if (!meetsGate)
        {
            priority = 0;
            severity = 1m - healthScore;
        }
        else if (deteriorating)
        {
            priority = 1;
            severity = decline;
        }
        else
        {
            return (int.MaxValue, 0m); // Not a candidate
        }

        // Low-ROI deprioritization
        if (approvalRate < 0.2)
        {
            priority += 2;
            severity *= 0.5m;
        }

        return (priority, severity);
    }
}
