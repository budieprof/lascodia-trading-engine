using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Services;

public sealed record RegimeArchetypeGateResult(bool Allowed, string? RejectionReason);

/// <summary>
/// Hard-gates signals whose strategy type isn't in the current-regime compatible list.
///
/// <para>
/// Today the engine uses <see cref="IRegimeStrategyMapper"/> at *generation* time to decide
/// which archetypes to screen for each regime, but once a strategy is Active it fires on
/// any regime. That's the wrong semantic: a trend-follower deployed in Trending is
/// meaningfully different from the same trend-follower still firing after the regime has
/// rotated to Ranging — the strategy's historical edge doesn't survive the regime change.
/// </para>
///
/// <para>
/// This filter applies the mapper's compatibility matrix at *signal-emission* time. Reads
/// the latest <see cref="MarketRegimeSnapshot"/> for the signal's symbol+timeframe, looks
/// up the regime's compatible-type list via the mapper, and rejects when the strategy's
/// type isn't present. Cold-start (no regime snapshot) passes through so the gate is
/// reject-only, matching the MTF filter's semantic.
/// </para>
///
/// <para>
/// Operators can disable via <c>RegimeArchetypeGate:Enabled=false</c> when bootstrapping
/// or running an intentionally regime-ignorant strategy.
/// </para>
/// </summary>
public interface IRegimeArchetypeGateFilter
{
    Task<RegimeArchetypeGateResult> CheckAsync(
        string symbol,
        Timeframe signalTimeframe,
        StrategyType strategyType,
        DateTime asOfUtc,
        CancellationToken ct);
}

[RegisterService(ServiceLifetime.Scoped, typeof(IRegimeArchetypeGateFilter))]
public sealed class RegimeArchetypeGateFilter : IRegimeArchetypeGateFilter
{
    private readonly IReadApplicationDbContext _readCtx;
    private readonly IRegimeStrategyMapper _mapper;
    private readonly ILogger<RegimeArchetypeGateFilter> _logger;

    private const string CK_Enabled  = "RegimeArchetypeGate:Enabled";
    private const string CK_MaxStale = "RegimeArchetypeGate:MaxSnapshotAgeHours";
    private const int    DefaultMaxStaleHours = 6;

    public RegimeArchetypeGateFilter(
        IReadApplicationDbContext readCtx,
        IRegimeStrategyMapper mapper,
        ILogger<RegimeArchetypeGateFilter> logger)
    {
        _readCtx = readCtx;
        _mapper  = mapper;
        _logger  = logger;
    }

    public async Task<RegimeArchetypeGateResult> CheckAsync(
        string symbol, Timeframe signalTimeframe, StrategyType strategyType,
        DateTime asOfUtc, CancellationToken ct)
    {
        var db = _readCtx.GetDbContext();

        bool enabled = await GetBoolAsync(db, CK_Enabled, defaultValue: true, ct);
        if (!enabled)
            return new RegimeArchetypeGateResult(true, null);

        int maxStaleHours = await GetIntAsync(db, CK_MaxStale, DefaultMaxStaleHours, ct);
        var cutoff = asOfUtc.AddHours(-maxStaleHours);

        var snap = await db.Set<MarketRegimeSnapshot>().AsNoTracking()
            .Where(r => r.Symbol == symbol
                     && r.Timeframe == signalTimeframe
                     && !r.IsDeleted
                     && r.DetectedAt >= cutoff)
            .OrderByDescending(r => r.DetectedAt)
            .Select(r => (LascodiaTradingEngine.Domain.Enums.MarketRegime?)r.Regime)
            .FirstOrDefaultAsync(ct);

        if (snap is null)
        {
            // Cold start — allow through. The filter is reject-only; data gaps must not
            // hard-reject, or a healthy broker connection can be paralysed by a lagging
            // regime detector.
            return new RegimeArchetypeGateResult(true, null);
        }

        var compatible = _mapper.GetStrategyTypes(snap.Value);
        if (compatible.Count == 0)
        {
            // Regime has no compatible types — e.g. Crisis. Reject hard.
            return new RegimeArchetypeGateResult(false,
                $"Regime {snap} on {symbol}/{signalTimeframe} has no compatible strategy types");
        }

        if (!compatible.Contains(strategyType))
        {
            return new RegimeArchetypeGateResult(false,
                $"Strategy type {strategyType} is not mapped to regime {snap} on {symbol}/{signalTimeframe}");
        }

        return new RegimeArchetypeGateResult(true, null);
    }

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
