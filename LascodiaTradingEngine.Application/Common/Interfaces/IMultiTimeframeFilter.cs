namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Validates trade signals against higher-timeframe trend alignment.
/// Prevents counter-trend entries by requiring confirmation from at least one higher timeframe.
/// </summary>
public interface IMultiTimeframeFilter
{
    /// <summary>Returns <c>true</c> if higher timeframes confirm the signal direction.</summary>
    Task<bool> IsConfirmedAsync(string symbol, string signalDirection, string primaryTimeframe, CancellationToken ct);

    /// <summary>
    /// Returns the ratio of higher timeframes that confirm the signal direction (0.0–1.0).
    /// Used by post-evaluator confidence modifiers to scale signal confidence based on
    /// multi-timeframe alignment strength rather than a binary pass/fail.
    /// </summary>
    Task<decimal> GetConfirmationStrengthAsync(string symbol, string signalDirection, string primaryTimeframe, CancellationToken ct);
}
