using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Resolves the currently active <see cref="MLCpcEncoder"/> for a (symbol, timeframe, regime)
/// triple. Consumed by both the training-side V7 feature builder and the inference-side V7
/// path in <c>MLSignalScorer</c> / <c>CompositeMLEvaluator</c> so the same encoder projection
/// is applied symmetrically.
///
/// <para>
/// When <paramref name="regime"/> (on <see cref="GetAsync"/>) is non-null, the provider first
/// looks up a regime-specific encoder; if none exists, it falls back to the global (null-regime)
/// encoder so a pair keeps scoring even before per-regime training has run.
/// </para>
/// </summary>
public interface IActiveCpcEncoderProvider
{
    /// <summary>
    /// Returns the active encoder with non-null <see cref="MLCpcEncoder.EncoderBytes"/>
    /// for the given (symbol, timeframe, regime), or <c>null</c> if no row is active.
    /// When <paramref name="regime"/> is non-null and no regime-specific row exists, falls
    /// back to the global (null-regime) encoder. Implementations cache the result with a
    /// short TTL; callers must not mutate the returned entity.
    /// </summary>
    Task<MLCpcEncoder?> GetAsync(
        string symbol,
        Timeframe timeframe,
        global::LascodiaTradingEngine.Domain.Enums.MarketRegime? regime,
        CancellationToken ct);
}
