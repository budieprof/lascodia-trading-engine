using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Encapsulates the data loading and preparation phase of the optimization pipeline.
/// Handles candle loading, regime-aware filtering with blend ratio, data quality
/// validation with holiday awareness, gap imputation, train/test splitting with
/// embargo, and transaction cost configuration from symbol metadata.
/// </summary>
/// <remarks>
/// This class serves as the logical boundary for data concerns. The actual
/// implementation lives in <c>OptimizationWorker.LoadAndValidateCandlesAsync</c>
/// and related methods. This facade exists for organizational clarity.
/// </remarks>
internal sealed class OptimizationDataLoader
{
    private readonly ILogger _logger;

    internal OptimizationDataLoader(ILogger logger) => _logger = logger;

    /// <summary>
    /// Computes the effective lookback period in months based on the strategy's timeframe.
    /// Higher timeframes need more months to accumulate enough candles for meaningful
    /// backtesting and cross-validation.
    /// </summary>
    internal static int ComputeEffectiveLookback(Timeframe timeframe, int configuredMonths)
    {
        if (configuredMonths != 6) return configuredMonths; // Explicit override

        return timeframe switch
        {
            Timeframe.D1  => 24,
            Timeframe.H4  => 12,
            Timeframe.H1  => 6,
            Timeframe.M15 => 3,
            Timeframe.M5  => 2,
            Timeframe.M1  => 2,
            _             => 6,
        };
    }
}
