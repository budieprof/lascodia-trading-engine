using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.RiskProfiles.Services;

/// <summary>
/// Validates trade signals and account drawdown against a given risk profile.
/// </summary>
public class RiskChecker : IRiskChecker
{
    public Task<RiskCheckResult> CheckAsync(
        TradeSignal signal,
        RiskProfile profile,
        CancellationToken cancellationToken)
    {
        // 1. Reject stale signals before doing anything else.
        if (signal.ExpiresAt <= DateTime.UtcNow)
            return Fail($"Signal {signal.Id} has expired (ExpiresAt={signal.ExpiresAt:u}).");

        // 2. Lot size must be positive.
        if (signal.SuggestedLotSize <= 0)
            return Fail($"SuggestedLotSize must be greater than zero (got {signal.SuggestedLotSize}).");

        // 3. Lot size cap.
        if (signal.SuggestedLotSize > profile.MaxLotSizePerTrade)
            return Fail($"Lot size {signal.SuggestedLotSize} exceeds profile maximum {profile.MaxLotSizePerTrade}.");

        // 4. Stop-loss / take-profit directional consistency.
        if (signal.Direction == TradeDirection.Buy)
        {
            if (signal.StopLoss.HasValue && signal.StopLoss >= signal.EntryPrice)
                return Fail($"Buy signal stop-loss ({signal.StopLoss}) must be below entry price ({signal.EntryPrice}).");

            if (signal.TakeProfit.HasValue && signal.TakeProfit <= signal.EntryPrice)
                return Fail($"Buy signal take-profit ({signal.TakeProfit}) must be above entry price ({signal.EntryPrice}).");
        }
        else // Sell
        {
            if (signal.StopLoss.HasValue && signal.StopLoss <= signal.EntryPrice)
                return Fail($"Sell signal stop-loss ({signal.StopLoss}) must be above entry price ({signal.EntryPrice}).");

            if (signal.TakeProfit.HasValue && signal.TakeProfit >= signal.EntryPrice)
                return Fail($"Sell signal take-profit ({signal.TakeProfit}) must be below entry price ({signal.EntryPrice}).");
        }

        // 5. ML agreement check: if an ML model scored this signal and its predicted direction
        //    disagrees with the strategy direction, only pass signals with high confidence.
        //    A disagreement on a low-confidence signal is a strong rejection signal.
        if (signal.MLPredictedDirection.HasValue &&
            signal.MLConfidenceScore.HasValue &&
            signal.MLPredictedDirection != signal.Direction)
        {
            const decimal mlDisagreementMinConfidence = 0.70m;
            if (signal.Confidence < mlDisagreementMinConfidence)
                return Fail(
                    $"ML model predicts {signal.MLPredictedDirection} but signal direction is {signal.Direction}; " +
                    $"signal confidence {signal.Confidence:P0} is below required {mlDisagreementMinConfidence:P0} for a disagreement override.");
        }

        return Pass();
    }

    /// <summary>
    /// Checks account-level drawdown against the profile limits.
    /// </summary>
    /// <remarks>
    /// This method is intentionally called twice by the pipeline with different arguments:
    /// <list type="bullet">
    ///   <item><b>Daily check</b>  — pass today's session-open balance as <paramref name="peakBalance"/>.</item>
    ///   <item><b>Total check</b>  — pass the all-time equity peak as <paramref name="peakBalance"/>.</item>
    /// </list>
    /// The <paramref name="profile"/> threshold used to evaluate each call (<see cref="RiskProfile.MaxDailyDrawdownPct"/>
    /// vs <see cref="RiskProfile.MaxTotalDrawdownPct"/>) must be selected by the caller to match the context.
    /// </remarks>
    public Task<RiskCheckResult> CheckDrawdownAsync(
        RiskProfile profile,
        decimal currentBalance,
        decimal peakBalance,
        CancellationToken cancellationToken)
    {
        if (currentBalance < 0)
            return Fail($"Current balance ({currentBalance}) is negative; trading halted.");

        if (peakBalance <= 0)
            return Pass(); // No meaningful peak yet — nothing to protect.

        decimal drawdownPct = (peakBalance - currentBalance) / peakBalance * 100m;

        // Daily drawdown gate.
        if (drawdownPct >= profile.MaxDailyDrawdownPct)
            return Fail(
                $"Daily drawdown of {drawdownPct:F2}% has reached or exceeded the profile limit of {profile.MaxDailyDrawdownPct}%. " +
                "No further orders will be placed today.");

        // Total (peak-to-trough) drawdown circuit-breaker.
        if (drawdownPct >= profile.MaxTotalDrawdownPct)
            return Fail(
                $"Total drawdown of {drawdownPct:F2}% has reached or exceeded the profile limit of {profile.MaxTotalDrawdownPct}%. " +
                "Automated trading is paused until the account recovers.");

        return Pass();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Task<RiskCheckResult> Fail(string reason) =>
        Task.FromResult(new RiskCheckResult(Passed: false, BlockReason: reason));

    private static Task<RiskCheckResult> Pass() =>
        Task.FromResult(new RiskCheckResult(Passed: true, BlockReason: null));
}
