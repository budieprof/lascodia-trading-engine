namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Checks whether high-impact economic news events are within the configured blackout window,
/// preventing signal generation during periods of elevated event risk.
/// </summary>
public interface INewsFilter
{
    /// <summary>
    /// Returns <c>true</c> if it is safe to trade (no high-impact news within the blackout window).
    /// </summary>
    /// <param name="symbol">Currency pair symbol whose base and quote currencies are checked.</param>
    /// <param name="tradeTime">UTC time of the proposed trade.</param>
    /// <param name="blackoutMinutesBefore">Minutes before the event to start the blackout.</param>
    /// <param name="blackoutMinutesAfter">Minutes after the event to end the blackout.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> IsSafeToTradeAsync(string symbol, DateTime tradeTime, int blackoutMinutesBefore, int blackoutMinutesAfter, CancellationToken ct);
}
