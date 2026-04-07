using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// A single composable step in the Tier 2 risk-check pipeline.
/// Each step validates one concern (margin, exposure, drawdown, spread, etc.)
/// and returns a pass/fail result.
/// </summary>
public interface IRiskCheckStep
{
    /// <summary>Human-readable name for logging and diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Evaluates the risk check step against the given signal and context.
    /// </summary>
    Task<RiskCheckStepResult> CheckAsync(TradeSignal signal, RiskCheckContext context, CancellationToken ct);
}

/// <summary>
/// Result of an individual risk check step.
/// </summary>
/// <param name="Passed"><c>true</c> if the check passed; <c>false</c> if it blocked the trade.</param>
/// <param name="BlockReason">Human-readable reason when <paramref name="Passed"/> is <c>false</c>.</param>
public record RiskCheckStepResult(bool Passed, string? BlockReason = null);
