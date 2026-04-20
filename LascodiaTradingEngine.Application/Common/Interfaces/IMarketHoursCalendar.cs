namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Answers "is the forex / CFD market closed for this symbol right now?" and,
/// when closed, "when does it reopen?".
///
/// <para>
/// Standard 24/5 forex closure is Friday ~22:00 UTC through Sunday ~22:00 UTC.
/// Implementations may override with broker-specific schedules loaded from
/// <c>TradingSessionSchedule</c> / <c>EngineConfig</c>.
/// </para>
///
/// <para>
/// Used by <see cref="Workers.StrategyWorker"/> to extend the TTL of signals
/// generated during a market-closed window so they survive to the next open
/// rather than expiring in bulk on Sunday evening.
/// </para>
/// </summary>
public interface IMarketHoursCalendar
{
    /// <summary>
    /// Returns <c>true</c> when the market for <paramref name="symbol"/> is closed at
    /// <paramref name="utcTime"/> — i.e. no active broker session accepts orders.
    /// </summary>
    bool IsMarketClosed(string symbol, DateTime utcTime);

    /// <summary>
    /// Returns the next market-open instant at or after <paramref name="utcTime"/>
    /// for <paramref name="symbol"/>. When the market is already open, returns
    /// <paramref name="utcTime"/> unchanged.
    /// </summary>
    DateTime NextMarketOpen(string symbol, DateTime utcTime);
}
