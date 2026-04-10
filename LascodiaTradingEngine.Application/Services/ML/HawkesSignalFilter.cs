using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Evaluates the current Hawkes process intensity λ(t) to detect burst episodes
/// and suppress new signals during cluster periods (Rec #32).
/// </summary>
[RegisterService]
public sealed class HawkesSignalFilter : IHawkesSignalFilter
{
    private readonly IReadApplicationDbContext _read;
    private readonly ILogger<HawkesSignalFilter> _logger;

    public HawkesSignalFilter(IReadApplicationDbContext read, ILogger<HawkesSignalFilter> logger)
    {
        _read   = read;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> IsBurstEpisodeAsync(
        string                  symbol,
        Timeframe               timeframe,
        IReadOnlyList<DateTime> recentSignalTimestamps,
        CancellationToken       ct = default)
    {
        var db = _read.GetDbContext();
        var kernel = await db.Set<MLHawkesKernelParams>()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe && !k.IsDeleted)
            .OrderByDescending(k => k.FittedAt)
            .FirstOrDefaultAsync(ct);

        if (kernel == null) return false;   // no kernel fitted yet — allow signal

        double nowHours = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600.0;
        double intensity = kernel.Mu;

        foreach (var ts in recentSignalTimestamps)
        {
            // Ensure UTC conversion regardless of the DateTime.Kind of the incoming timestamp.
            // If Kind is Local, convert to UTC first; if Unspecified, treat as UTC.
            DateTime utcTs = ts.Kind == DateTimeKind.Local ? ts.ToUniversalTime() : DateTime.SpecifyKind(ts, DateTimeKind.Utc);
            double eventHours = new DateTimeOffset(utcTs, TimeSpan.Zero).ToUnixTimeSeconds() / 3600.0;
            double dt = nowHours - eventHours;
            if (dt < 0) continue;
            intensity += kernel.Alpha * Math.Exp(-kernel.Beta * dt);
        }

        bool isBurst = intensity > kernel.Mu * kernel.SuppressMultiplier;
        if (isBurst)
            _logger.LogDebug(
                "HawkesSignalFilter burst for {Symbol}/{TF}: λ={L:F3} > {T:F3}",
                symbol, timeframe, intensity, kernel.Mu * kernel.SuppressMultiplier);

        return isBurst;
    }
}
