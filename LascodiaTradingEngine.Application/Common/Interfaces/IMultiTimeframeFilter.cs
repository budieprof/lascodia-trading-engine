namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface IMultiTimeframeFilter
{
    /// Returns true if higher timeframes confirm the signal direction.
    Task<bool> IsConfirmedAsync(string symbol, string signalDirection, string primaryTimeframe, CancellationToken ct);
}
