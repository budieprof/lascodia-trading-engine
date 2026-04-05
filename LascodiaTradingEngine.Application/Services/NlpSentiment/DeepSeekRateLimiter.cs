using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Options;

namespace LascodiaTradingEngine.Application.Services.NlpSentiment;

/// <summary>
/// Singleton rate limiter for DeepSeek API calls using a sliding one-hour window.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public sealed class DeepSeekRateLimiter
{
    private readonly Queue<DateTime> _callLog = new();
    private readonly object _lock = new();
    private readonly int _maxCallsPerHour;

    public DeepSeekRateLimiter(DeepSeekOptions options)
    {
        _maxCallsPerHour = options.MaxCallsPerHour > 0 ? options.MaxCallsPerHour : 60;
    }

    public bool TryAcquire()
    {
        lock (_lock)
        {
            PurgeOldEntries();
            if (_callLog.Count >= _maxCallsPerHour) return false;
            _callLog.Enqueue(DateTime.UtcNow);
            return true;
        }
    }

    public int RemainingCallsThisHour
    {
        get
        {
            lock (_lock)
            {
                PurgeOldEntries();
                return Math.Max(0, _maxCallsPerHour - _callLog.Count);
            }
        }
    }

    private void PurgeOldEntries()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        while (_callLog.Count > 0 && _callLog.Peek() < cutoff)
            _callLog.Dequeue();
    }
}
