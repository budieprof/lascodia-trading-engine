using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services.RateLimiting;

[RegisterService(ServiceLifetime.Singleton)]
public class TokenBucketRateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _buckets = new();

    public Task<bool> TryAcquireAsync(string key, int maxRequests, TimeSpan window, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var allowed = false;

        _buckets.AddOrUpdate(
            key,
            addValueFactory: _ =>
            {
                allowed = true;
                return (1, now);
            },
            updateValueFactory: (_, existing) =>
            {
                if (now - existing.WindowStart > window)
                {
                    allowed = true;
                    return (1, now);
                }

                if (existing.Count < maxRequests)
                {
                    allowed = true;
                    return (existing.Count + 1, existing.WindowStart);
                }

                // Rate limited
                allowed = false;
                return existing;
            });

        return Task.FromResult(allowed);
    }

    public Task<int> GetRemainingAsync(string key, int maxRequests, TimeSpan window, CancellationToken ct)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
            return Task.FromResult(maxRequests);

        if (DateTime.UtcNow - bucket.WindowStart > window)
            return Task.FromResult(maxRequests);

        return Task.FromResult(Math.Max(0, maxRequests - bucket.Count));
    }
}
