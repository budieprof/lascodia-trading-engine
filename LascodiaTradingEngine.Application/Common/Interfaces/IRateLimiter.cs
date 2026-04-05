namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Token-bucket rate limiter keyed by arbitrary string identifiers (e.g. EA instance ID + endpoint).
/// </summary>
public interface IRateLimiter
{
    /// <summary>Returns <c>true</c> if the request is allowed (not throttled).</summary>
    Task<bool> TryAcquireAsync(string key, int maxRequests, TimeSpan window, CancellationToken ct);

    /// <summary>Returns the number of remaining allowed requests for the given key within the current window.</summary>
    Task<int> GetRemainingAsync(string key, int maxRequests, TimeSpan window, CancellationToken ct);
}
