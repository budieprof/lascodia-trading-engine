using LascodiaTradingEngine.Application.Backtesting.Services;

namespace LascodiaTradingEngine.Application.Backtesting.Models;

/// <summary>
/// Deterministic snapshot of the transaction-cost model resolved when a validation run is queued.
/// Stored on BacktestRun/WalkForwardRun so execution remains reproducible even if config or symbol
/// metadata changes before the worker actually runs.
/// </summary>
public sealed class BacktestOptionsSnapshot
{
    public decimal SpreadPriceUnits { get; init; }
    public decimal CommissionPerLot { get; init; }
    public decimal SlippagePriceUnits { get; init; }
    public decimal SwapPerLotPerDay { get; init; }
    public decimal ContractSize { get; init; } = 100_000m;
    /// <summary>
    /// Pip size in price units for PnL normalisation. Defaults to 0.0001 (EURUSD-style 4-decimal
    /// pairs). JPY pairs need 0.01. Without this, JPY-pair PnL is ~100× inflated because 1 pip
    /// ≠ 0.0001 for those pairs — see <c>BacktestEngine.CalculatePnL</c>.
    /// </summary>
    public decimal PipSizeInPriceUnits { get; init; } = 0.0001m;
    public decimal GapSlippagePct { get; init; }
    public decimal FillRatio { get; init; } = 1.0m;
    public List<SpreadBucketSnapshot> SpreadBuckets { get; init; } = [];

    public BacktestOptions ToOptions()
    {
        var options = new BacktestOptions
        {
            SpreadPriceUnits = SpreadPriceUnits,
            CommissionPerLot = CommissionPerLot,
            SlippagePriceUnits = SlippagePriceUnits,
            SwapPerLotPerDay = SwapPerLotPerDay,
            ContractSize = ContractSize,
            PipSizeInPriceUnits = PipSizeInPriceUnits <= 0m ? 0.0001m : PipSizeInPriceUnits,
            GapSlippagePct = GapSlippagePct,
            FillRatio = FillRatio <= 0m ? 1.0m : FillRatio,
        };

        if (SpreadBuckets.Count == 0)
            return options;

        var spreadLookup = SpreadBuckets
            .GroupBy(bucket => (bucket.HourUtc, bucket.DayOfWeek))
            .ToDictionary(group => group.Key, group => group.Last().SpreadPriceUnits);

        options.SpreadFunction = barTimestamp =>
        {
            if (spreadLookup.TryGetValue((barTimestamp.Hour, barTimestamp.DayOfWeek), out decimal exactSpread))
                return exactSpread;

            if (spreadLookup.TryGetValue((barTimestamp.Hour, null), out decimal hourSpread))
                return hourSpread;

            return SpreadPriceUnits;
        };

        return options;
    }
}

public sealed class SpreadBucketSnapshot
{
    public int HourUtc { get; init; }
    public DayOfWeek? DayOfWeek { get; init; }
    public decimal SpreadPriceUnits { get; init; }
}
