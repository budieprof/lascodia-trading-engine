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
    private const string CK_MaxKernelAgeHours = "MLHawkes:MaxKernelAgeHours";
    internal static readonly TimeSpan MaxKernelAge = TimeSpan.FromHours(48);

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
        string normalizedSymbol = NormalizeSymbol(symbol);
        TimeSpan maxKernelAge = await GetMaxKernelAgeAsync(db, ct);
        var kernel = await db.Set<MLHawkesKernelParams>()
            .Where(k => k.Symbol == normalizedSymbol && k.Timeframe == timeframe && !k.IsDeleted)
            .OrderByDescending(k => k.FittedAt)
            .FirstOrDefaultAsync(ct);

        if (kernel == null) return false;   // no kernel fitted yet — allow signal
        if (DateTime.UtcNow - kernel.FittedAt > maxKernelAge)
        {
            _logger.LogDebug(
                "HawkesSignalFilter ignoring stale kernel for {Symbol}/{TF}: fitted at {FittedAt:u}",
                normalizedSymbol, timeframe, kernel.FittedAt);
            return false;
        }

        if (!double.IsFinite(kernel.Mu) || !double.IsFinite(kernel.Alpha) || !double.IsFinite(kernel.Beta)
            || kernel.Mu <= 0 || kernel.Alpha <= 0 || kernel.Beta <= 0 || kernel.Alpha >= kernel.Beta)
        {
            _logger.LogWarning(
                "HawkesSignalFilter ignoring invalid kernel for {Symbol}/{TF}: μ={Mu} α={Alpha} β={Beta}",
                normalizedSymbol, timeframe, kernel.Mu, kernel.Alpha, kernel.Beta);
            return false;
        }

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
                normalizedSymbol, timeframe, intensity, kernel.Mu * kernel.SuppressMultiplier);

        return isBurst;
    }

    private static async Task<TimeSpan> GetMaxKernelAgeAsync(DbContext db, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key == CK_MaxKernelAgeHours)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);

        return int.TryParse(raw, out int hours)
            ? TimeSpan.FromHours(Math.Clamp(hours, 1, 24 * 30))
            : MaxKernelAge;
    }

    private static string NormalizeSymbol(string symbol)
        => symbol.Trim().ToUpperInvariant();
}
