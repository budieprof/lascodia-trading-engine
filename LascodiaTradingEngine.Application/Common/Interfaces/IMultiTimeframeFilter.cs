namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface IMultiTimeframeFilter
{
    /// Returns true if higher timeframes confirm the signal direction.
    Task<bool> IsConfirmedAsync(string symbol, string signalDirection, string primaryTimeframe, CancellationToken ct);

    /// <summary>
    /// Returns the ratio of higher timeframes that confirm the signal direction (0.0–1.0).
    /// Used by post-evaluator confidence modifiers to scale signal confidence based on
    /// multi-timeframe alignment strength rather than a binary pass/fail.
    /// </summary>
    Task<decimal> GetConfirmationStrengthAsync(string symbol, string signalDirection, string primaryTimeframe, CancellationToken ct);
}
