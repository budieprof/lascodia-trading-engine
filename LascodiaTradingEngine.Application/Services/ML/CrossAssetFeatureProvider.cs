using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Point-in-time cross-asset feature provider — scaffolding for the next feature-vector
/// version (V3). The production engine ships with V1 (33 features) and V2 (37 with
/// intra-FX basket macros). V3 extends V2 with exogenous signals that are genuinely
/// non-FX: the dollar index, long-rate yields, and equity-volatility proxies.
///
/// <para>
/// This class returns a <see cref="CrossAssetSnapshot"/> as-of a given timestamp,
/// populated from <see cref="Candle"/> rows tagged with the relevant cross-asset
/// symbols. Callers must ensure those candles are ingested (e.g. DXY futures,
/// 10Y yield proxies, VIX-equivalent). When a symbol is missing the value is zero,
/// which the V3 feature builder treats as a neutral contribution.
/// </para>
///
/// <para>
/// Activation path:
/// <list type="number">
///   <item>Ingest candles for <c>DXY</c>, <c>US10Y</c>, <c>VIX</c> (or proxies)
///         via the existing EA or a dedicated feed worker.</item>
///   <item>Set <c>MLTraining:UseCrossAssetFeatureVector=true</c> in EngineConfig.</item>
///   <item>Add <c>BuildFeatureVectorV3</c> in <c>MLFeatureHelper</c> that consumes
///         this provider. <see cref="FeatureCountV3"/> = 40.</item>
///   <item>Extend <c>CompositeMLEvaluator</c> and <c>MLSignalScorer</c> dispatch
///         with a V3 branch mirroring the existing V2 block.</item>
/// </list>
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public sealed class CrossAssetFeatureProvider
{
    /// <summary>Feature count exposed to callers when V3 is enabled.</summary>
    public const int FeatureCountV3 = 40;

    /// <summary>Number of new features V3 adds on top of V2.</summary>
    public const int CrossAssetSlotCount = 3;

    /// <summary>Names of the V3 cross-asset features, in slot order.</summary>
    public static readonly string[] CrossAssetFeatureNames =
    [
        "DxyReturn5d",        // 5-day percent change in the US Dollar Index
        "Us10YYieldChange5d", // 5-day basis-point change in the 10Y US Treasury yield
        "VixLevelNormalized", // VIX level normalized to [-1, 1] over a 1-year lookback
    ];

    private readonly IServiceScopeFactory _scopeFactory;

    public CrossAssetFeatureProvider(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Returns the cross-asset snapshot as-of the supplied timestamp. Zero values
    /// when the underlying symbol series is missing or insufficient history exists.
    /// </summary>
    public async Task<CrossAssetSnapshot> GetAsync(DateTime asOfUtc, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db = readCtx.GetDbContext();

        float dxy  = await ComputeReturn5dAsync(db, "DXY", asOfUtc, ct);
        float y10  = await ComputeReturn5dAsync(db, "US10Y", asOfUtc, ct);
        float vix  = await ComputeNormalizedLevelAsync(db, "VIX", asOfUtc, 252, ct);

        return new CrossAssetSnapshot(dxy, y10, vix);
    }

    private static async Task<float> ComputeReturn5dAsync(
        DbContext db, string symbol, DateTime asOfUtc, CancellationToken ct)
    {
        var closes = await db.Set<Candle>()
            .AsNoTracking()
            .Where(c => c.Symbol == symbol
                     && c.Timeframe == Timeframe.D1
                     && c.IsClosed
                     && !c.IsDeleted
                     && c.Timestamp <= asOfUtc)
            .OrderByDescending(c => c.Timestamp)
            .Take(6)
            .Select(c => c.Close)
            .ToListAsync(ct);

        if (closes.Count < 6 || closes[5] == 0m) return 0f;
        return (float)((double)(closes[0] - closes[5]) / (double)closes[5]);
    }

    private static async Task<float> ComputeNormalizedLevelAsync(
        DbContext db, string symbol, DateTime asOfUtc, int lookbackBars, CancellationToken ct)
    {
        var closes = await db.Set<Candle>()
            .AsNoTracking()
            .Where(c => c.Symbol == symbol
                     && c.Timeframe == Timeframe.D1
                     && c.IsClosed
                     && !c.IsDeleted
                     && c.Timestamp <= asOfUtc)
            .OrderByDescending(c => c.Timestamp)
            .Take(lookbackBars)
            .Select(c => (double)c.Close)
            .ToListAsync(ct);

        if (closes.Count < 20) return 0f;
        double current = closes[0];
        double min = closes.Min();
        double max = closes.Max();
        if (max - min < 1e-9) return 0f;
        // Normalize to [-1, 1]: (current - midpoint) / half-range
        double mid = (min + max) / 2.0;
        double halfRange = (max - min) / 2.0;
        return (float)Math.Clamp((current - mid) / halfRange, -1.0, 1.0);
    }
}

/// <summary>
/// Point-in-time snapshot of cross-asset signals. Each field is a bounded,
/// normalised scalar suitable for direct inclusion in a feature vector.
/// </summary>
public readonly record struct CrossAssetSnapshot(
    float DxyReturn5d,
    float Us10YYieldChange5d,
    float VixLevelNormalized);
