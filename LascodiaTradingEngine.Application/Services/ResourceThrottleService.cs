using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Singleton resource throttle that limits total concurrent CPU-bound operations across
/// all workers (backtesting, ML training, optimization). Uses a SemaphoreSlim to cap
/// concurrent slots to <c>Environment.ProcessorCount - 2</c> (min 2).
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class ResourceThrottleService : IResourceThrottleService
{
    private readonly SemaphoreSlim _semaphore;
    private readonly ILogger<ResourceThrottleService> _logger;
    private int _activeSlots;

    public int MaxSlots { get; }
    public int ActiveSlots => Volatile.Read(ref _activeSlots);

    public ResourceThrottleService(ILogger<ResourceThrottleService> logger)
    {
        _logger = logger;
        MaxSlots = Math.Max(2, Environment.ProcessorCount - 2);
        _semaphore = new SemaphoreSlim(MaxSlots, MaxSlots);
    }

    public async Task<IDisposable?> TryAcquireCpuSlotAsync(string workerName, CancellationToken ct = default)
    {
        bool acquired = await _semaphore.WaitAsync(0, ct);
        if (!acquired)
        {
            _logger.LogDebug(
                "ResourceThrottle: {Worker} could not acquire CPU slot ({Active}/{Max} in use)",
                workerName, ActiveSlots, MaxSlots);
            return null;
        }

        Interlocked.Increment(ref _activeSlots);
        _logger.LogDebug(
            "ResourceThrottle: {Worker} acquired CPU slot ({Active}/{Max} in use)",
            workerName, ActiveSlots, MaxSlots);

        return new SlotHandle(this, workerName);
    }

    private void Release(string workerName)
    {
        Interlocked.Decrement(ref _activeSlots);
        _semaphore.Release();
        _logger.LogDebug(
            "ResourceThrottle: {Worker} released CPU slot ({Active}/{Max} in use)",
            workerName, ActiveSlots, MaxSlots);
    }

    private sealed class SlotHandle : IDisposable
    {
        private readonly ResourceThrottleService _parent;
        private readonly string _workerName;
        private int _disposed;

        public SlotHandle(ResourceThrottleService parent, string workerName)
        {
            _parent = parent;
            _workerName = workerName;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _parent.Release(_workerName);
        }
    }
}
