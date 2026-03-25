namespace LascodiaTradingEngine.Application.Services.EconomicCalendar;

/// <summary>
/// Singleton throttle that caps concurrent HTTP requests to ForexFactory and enforces
/// a minimum delay between requests to avoid triggering rate limits.
/// Registered as a singleton in DI so all scoped <see cref="ForexFactoryCalendarFeed"/>
/// instances share the same rate limit. Tests can inject their own instance for isolation.
/// </summary>
public sealed class ForexFactoryFetchThrottle
{
    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _minDelay;
    private readonly TimeProvider _timeProvider;
    private long _lastRequestTicks;
    private readonly object _timeLock = new();

    public ForexFactoryFetchThrottle(
        TimeProvider timeProvider,
        int maxConcurrency = 2,
        int minDelayMs = 800)
    {
        _timeProvider = timeProvider;
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _minDelay = TimeSpan.FromMilliseconds(minDelayMs);
        _lastRequestTicks = 0;
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);

        // Enforce minimum delay with random jitter to avoid predictable request patterns
        TimeSpan waitTime;
        lock (_timeLock)
        {
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)(_minDelay.TotalMilliseconds * 0.5)));
            var targetDelay = _minDelay + jitter;
            var elapsed = _timeProvider.GetElapsedTime(_lastRequestTicks);
            waitTime = elapsed < targetDelay ? targetDelay - elapsed : TimeSpan.Zero;
        }

        if (waitTime > TimeSpan.Zero)
            await Task.Delay(waitTime, ct);
    }

    public void Release()
    {
        lock (_timeLock)
        {
            _lastRequestTicks = _timeProvider.GetTimestamp();
        }
        _semaphore.Release();
    }
}
