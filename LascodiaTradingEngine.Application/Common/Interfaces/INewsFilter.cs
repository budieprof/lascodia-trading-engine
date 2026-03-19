namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface INewsFilter
{
    /// Returns true if it is safe to trade (no high-impact news within the blackout window).
    Task<bool> IsSafeToTradeAsync(string symbol, DateTime tradeTime, int blackoutMinutesBefore, int blackoutMinutesAfter, CancellationToken ct);
}
