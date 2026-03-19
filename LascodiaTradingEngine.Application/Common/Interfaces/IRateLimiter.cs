namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface IRateLimiter
{
    /// <summary>Returns true if the request is allowed (not throttled).</summary>
    Task<bool> TryAcquireAsync(string key, int maxRequests, TimeSpan window, CancellationToken ct);

    Task<int> GetRemainingAsync(string key, int maxRequests, TimeSpan window, CancellationToken ct);
}
