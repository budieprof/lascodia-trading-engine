using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.RiskProfiles.Services;

/// <summary>
/// Tier 1 signal validator — validates a trade signal's intrinsic quality without
/// any account state. Checks entry price positivity, signal expiry, lot size positivity,
/// SL/TP consistency, minimum SL distance, risk-reward ratio, mandatory stop-loss, and ML agreement.
/// </summary>
[RegisterService]
public class SignalValidator : ISignalValidator
{
    private readonly RiskCheckerOptions _options;
    private readonly TimeProvider _timeProvider;

    public SignalValidator(RiskCheckerOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public Task<RiskCheckResult> ValidateAsync(
        TradeSignal signal,
        SignalValidationContext context,
        CancellationToken cancellationToken)
    {
        var profile = context.Profile;

        // ── 0. Reject NaN/Infinity in numeric fields ─────────────────────
        if (!IsFinite(signal.EntryPrice) ||
            (signal.StopLoss.HasValue && !IsFinite(signal.StopLoss.Value)) ||
            (signal.TakeProfit.HasValue && !IsFinite(signal.TakeProfit.Value)) ||
            !IsFinite(signal.SuggestedLotSize) ||
            !IsFinite(signal.Confidence))
            return Fail("Signal contains non-finite numeric values (NaN or Infinity).");

        // ── 1. Entry price must be positive ───────────────────────────────
        if (signal.EntryPrice <= 0)
            return Fail($"EntryPrice must be greater than zero (got {signal.EntryPrice}).");

        // ── 2. Signal expiry ────────────────────────────────────────────────
        if (signal.ExpiresAt <= _timeProvider.GetUtcNow().UtcDateTime)
            return Fail($"Signal {signal.Id} has expired (ExpiresAt={signal.ExpiresAt:u}).");

        // ── 3. Lot size must be positive ────────────────────────────────────
        if (signal.SuggestedLotSize <= 0)
            return Fail($"SuggestedLotSize must be greater than zero (got {signal.SuggestedLotSize}).");

        // ── 4. Mandatory stop-loss enforcement ──────────────────────────────
        if (profile.RequireStopLoss && !signal.StopLoss.HasValue)
            return Fail("Risk profile requires a stop-loss but the signal has none.");

        // ── 5. SL/TP directional consistency ────────────────────────────────
        if (signal.Direction == TradeDirection.Buy)
        {
            if (signal.StopLoss.HasValue && signal.StopLoss >= signal.EntryPrice)
                return Fail($"Buy signal stop-loss ({signal.StopLoss}) must be below entry price ({signal.EntryPrice}).");
            if (signal.TakeProfit.HasValue && signal.TakeProfit <= signal.EntryPrice)
                return Fail($"Buy signal take-profit ({signal.TakeProfit}) must be above entry price ({signal.EntryPrice}).");
        }
        else
        {
            if (signal.StopLoss.HasValue && signal.StopLoss <= signal.EntryPrice)
                return Fail($"Sell signal stop-loss ({signal.StopLoss}) must be above entry price ({signal.EntryPrice}).");
            if (signal.TakeProfit.HasValue && signal.TakeProfit >= signal.EntryPrice)
                return Fail($"Sell signal take-profit ({signal.TakeProfit}) must be below entry price ({signal.EntryPrice}).");
        }

        // ── 6. Minimum stop-loss distance ───────────────────────────────────
        if (signal.StopLoss.HasValue && profile.MinStopLossDistancePips > 0)
        {
            decimal pipSize = GetPipSize(context.SymbolSpec);
            if (pipSize > 0)
            {
                decimal distancePips = Math.Abs(signal.EntryPrice - signal.StopLoss.Value) / pipSize;

                if (distancePips < profile.MinStopLossDistancePips)
                    return Fail(
                        $"Stop-loss distance of {distancePips:F1} pips is below the minimum of " +
                        $"{profile.MinStopLossDistancePips} pips. Tight stops are likely to be hit by spread/slippage.");
            }
        }

        // ── 7. Risk-reward ratio enforcement ────────────────────────────────
        if (signal.StopLoss.HasValue && signal.TakeProfit.HasValue && profile.MinRiskRewardRatio > 0)
        {
            decimal riskDistance = Math.Abs(signal.EntryPrice - signal.StopLoss.Value);
            decimal rewardDistance = Math.Abs(signal.TakeProfit.Value - signal.EntryPrice);

            if (riskDistance > 0)
            {
                decimal rrRatio = rewardDistance / riskDistance;
                if (rrRatio < profile.MinRiskRewardRatio)
                    return Fail(
                        $"Risk-reward ratio of {rrRatio:F2} is below the minimum of {profile.MinRiskRewardRatio:F2} " +
                        $"(risk={riskDistance}, reward={rewardDistance}).");
            }
        }

        // ── 8. ML agreement check ───────────────────────────────────────────
        if (signal.MLPredictedDirection.HasValue &&
            signal.MLConfidenceScore.HasValue &&
            signal.MLPredictedDirection != signal.Direction)
        {
            if (signal.Confidence < _options.MLDisagreementMinConfidence)
                return Fail(
                    $"ML model predicts {signal.MLPredictedDirection} but signal direction is {signal.Direction}; " +
                    $"signal confidence {signal.Confidence:P0} is below required {_options.MLDisagreementMinConfidence:P0} for a disagreement override.");
        }

        return Pass();
    }

    /// <summary>
    /// Returns the pip size for the given symbol specification.
    /// Uses the explicit <see cref="CurrencyPair.PipSize"/> if set, otherwise
    /// derives from <see cref="CurrencyPair.DecimalPlaces"/>.
    /// </summary>
    private static decimal GetPipSize(CurrencyPair? spec)
    {
        if (spec is null) return 0.0001m;

        if (spec.PipSize > 0)
            return spec.PipSize;

        return spec.DecimalPlaces > 0
            ? (decimal)Math.Pow(10, -(spec.DecimalPlaces - 1))
            : 0.0001m;
    }

    /// <summary>
    /// Returns true if the decimal value is a normal finite number.
    /// While C# decimals cannot represent NaN/Infinity natively, values
    /// converted from doubles or deserialized from external sources could
    /// arrive as extreme magnitudes. This also guards against zero-division
    /// artifacts in upstream calculations.
    /// </summary>
    private static bool IsFinite(decimal value)
    {
        // decimal cannot represent NaN/Infinity, but guard against extreme values
        // that would indicate upstream calculation errors
        return value is > decimal.MinValue and < decimal.MaxValue;
    }

    private static Task<RiskCheckResult> Fail(string reason) =>
        Task.FromResult(new RiskCheckResult(Passed: false, BlockReason: reason));

    private static Task<RiskCheckResult> Pass() =>
        Task.FromResult(new RiskCheckResult(Passed: true, BlockReason: null));
}
