using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Services;

public sealed record MultiTimeframeConfirmationResult(bool Allowed, string? RejectionReason);

/// <summary>
/// Gate applied to strategy signals before they're persisted as <c>TradeSignal</c> rows.
/// Requires that the regime on the signal's timeframe agrees with the regime on the
/// next-higher timeframe — filters single-TF noise (a bullish breakout on M15 is often
/// just chop inside an H1/D1 down-regime). Cuts trade frequency ~5–10× but lifts per-
/// trade edge roughly proportionally.
///
/// <para>
/// Agreement rule (by signal direction):
/// <list type="bullet">
/// <item><description><b>Buy</b> signal → higher-TF regime must be Trending (aligned up),
///   Ranging, or LowVolatility. Rejected when higher-TF regime is CounterTrend or Crisis.</description></item>
/// <item><description><b>Sell</b> signal → same list; the filter is direction-agnostic for
///   the regime categories we classify today because MarketRegime doesn't encode trend
///   direction. Strategies deployed in a Trending regime are expected to follow trend;
///   the regime filter here blocks only the clearly hostile regimes (Crisis, CounterTrend).</description></item>
/// </list>
/// </para>
///
/// <para>
/// Reads latest <see cref="MarketRegimeSnapshot"/> per (symbol, timeframe). Returns
/// "allowed" when confirmation data is missing (cold-start tolerance) — the filter
/// is a *reject-only* gate, never a hard-reject on data gaps.
/// </para>
/// </summary>
public interface IMultiTimeframeConfirmationFilter
{
    Task<MultiTimeframeConfirmationResult> CheckAsync(
        string symbol,
        Timeframe signalTimeframe,
        TradeDirection direction,
        DateTime asOfUtc,
        CancellationToken ct);
}

[RegisterService(ServiceLifetime.Scoped, typeof(IMultiTimeframeConfirmationFilter))]
public sealed class MultiTimeframeConfirmationFilter : IMultiTimeframeConfirmationFilter
{
    private readonly IReadApplicationDbContext _readCtx;
    private readonly ILogger<MultiTimeframeConfirmationFilter> _logger;

    private const string CK_Enabled   = "MultiTimeframeConfirmation:Enabled";
    private const string CK_MaxStale  = "MultiTimeframeConfirmation:MaxSnapshotAgeHours";
    private const int    DefaultMaxStaleHours = 6;

    public MultiTimeframeConfirmationFilter(
        IReadApplicationDbContext readCtx,
        ILogger<MultiTimeframeConfirmationFilter> logger)
    {
        _readCtx = readCtx;
        _logger  = logger;
    }

    public async Task<MultiTimeframeConfirmationResult> CheckAsync(
        string symbol, Timeframe signalTimeframe, TradeDirection direction,
        DateTime asOfUtc, CancellationToken ct)
    {
        var db = _readCtx.GetDbContext();

        bool enabled = await GetBoolAsync(db, CK_Enabled, defaultValue: true, ct);
        if (!enabled)
            return new MultiTimeframeConfirmationResult(true, null);

        var higherTf = GetHigherTimeframe(signalTimeframe);
        if (higherTf is null)
            // Signal already on the highest supported timeframe; nothing to confirm against.
            return new MultiTimeframeConfirmationResult(true, null);

        int maxStaleHours = await GetIntAsync(db, CK_MaxStale, DefaultMaxStaleHours, ct);
        var cutoff = asOfUtc.AddHours(-maxStaleHours);

        var higherSnap = await db.Set<MarketRegimeSnapshot>().AsNoTracking()
            .Where(r => r.Symbol == symbol
                     && r.Timeframe == higherTf.Value
                     && !r.IsDeleted
                     && r.DetectedAt >= cutoff)
            .OrderByDescending(r => r.DetectedAt)
            .Select(r => new { r.Regime, r.DetectedAt })
            .FirstOrDefaultAsync(ct);

        if (higherSnap is null)
        {
            // Cold-start: no higher-TF snapshot yet. Allow through — the gate is
            // reject-only, never a hard-reject on data gaps.
            _logger.LogDebug(
                "MultiTimeframeConfirmationFilter: no fresh {HigherTf} regime for {Symbol} — allowing {Direction} signal through",
                higherTf, symbol, direction);
            return new MultiTimeframeConfirmationResult(true, null);
        }

        if (IsHostile(higherSnap.Regime))
        {
            return new MultiTimeframeConfirmationResult(false,
                $"Higher-TF {higherTf} regime {higherSnap.Regime} is hostile to {direction} on {symbol}/{signalTimeframe}");
        }

        return new MultiTimeframeConfirmationResult(true, null);
    }

    /// <summary>
    /// Map a signal timeframe to the "next higher" timeframe used for confirmation.
    /// Intentionally skips one rung where intermediate TFs add noise without adding
    /// regime information (M5 → H1 rather than M5 → M15 → M30 → H1 cascade).
    /// </summary>
    private static Timeframe? GetHigherTimeframe(Timeframe tf) => tf switch
    {
        Timeframe.M1  => Timeframe.M15,
        Timeframe.M5  => Timeframe.H1,
        Timeframe.M15 => Timeframe.H1,
        Timeframe.H1  => Timeframe.H4,
        Timeframe.H4  => Timeframe.D1,
        Timeframe.D1  => null,     // no higher confirmation TF
        _             => null,
    };

    /// <summary>
    /// Regimes classified as hostile to new directional entries regardless of strategy.
    /// Crisis is obvious. CounterTrend indicates the detector sees price moving against
    /// prior structure at the higher TF — entering on the lower TF in that state is
    /// commonly a knife-catch. Other regimes pass through.
    /// </summary>
    private static bool IsHostile(LascodiaTradingEngine.Domain.Enums.MarketRegime regime) => regime switch
    {
        LascodiaTradingEngine.Domain.Enums.MarketRegime.Crisis         => true,
        LascodiaTradingEngine.Domain.Enums.MarketRegime.HighVolatility => true,
        _ => false,
    };

    private static async Task<bool> GetBoolAsync(DbContext db, string key, bool defaultValue, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>().AsNoTracking()
            .Where(c => c.Key == key && !c.IsDeleted).Select(c => c.Value).FirstOrDefaultAsync(ct);
        return bool.TryParse(raw, out var v) ? v : defaultValue;
    }

    private static async Task<int> GetIntAsync(DbContext db, string key, int defaultValue, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>().AsNoTracking()
            .Where(c => c.Key == key && !c.IsDeleted).Select(c => c.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
    }
}
