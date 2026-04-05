using LascodiaTradingEngine.Application.StrategyGeneration;

namespace LascodiaTradingEngine.Application.Services;

public sealed record HaircutRatios(
    double WinRateHaircut,
    double ProfitFactorHaircut,
    double SharpeHaircut,
    double DrawdownInflation,
    int SampleCount)
{
    public static readonly HaircutRatios Neutral = new(1.0, 1.0, 1.0, 1.0, 0);
}

public interface ILivePerformanceBenchmark
{
    Task<HaircutRatios> ComputeHaircutsAsync(CancellationToken ct);
    Task<HaircutRatios> GetCachedHaircutsAsync(CancellationToken ct);

    /// <summary>
    /// Computes synthetic haircut ratios from IS→OOS degradation in ScreeningMetrics
    /// when insufficient live trading data exists. Returns Neutral if no OOS metrics.
    /// SampleCount is negative to distinguish from live-computed haircuts.
    /// </summary>
    Task<HaircutRatios> ComputeBootstrappedHaircutsAsync(CancellationToken ct);
}
