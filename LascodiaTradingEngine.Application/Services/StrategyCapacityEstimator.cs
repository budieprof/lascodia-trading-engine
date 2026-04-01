using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Estimates strategy capital capacity using historical volume participation rates
/// and market impact curves calibrated from execution quality data.
/// Uses a power-law model: slippage = k × lots^α where k and α are calibrated.
/// </summary>
[RegisterService]
public class StrategyCapacityEstimator : IStrategyCapacityEstimator
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly ILogger<StrategyCapacityEstimator> _logger;

    /// <summary>Default calibration window in days.</summary>
    private const int DefaultCalibrationDays = 90;

    /// <summary>Minimum data points for reliable capacity estimation.</summary>
    private const int MinDataPoints = 20;

    public StrategyCapacityEstimator(
        IReadApplicationDbContext readContext,
        ILogger<StrategyCapacityEstimator> logger)
    {
        _readContext = readContext;
        _logger      = logger;
    }

    public async Task<StrategyCapacity> EstimateAsync(
        Strategy strategy,
        CancellationToken cancellationToken)
    {
        var ctx = _readContext.GetDbContext();
        var cutoff = DateTime.UtcNow.AddDays(-DefaultCalibrationDays);

        // Get execution quality data with order quantity for impact curve calibration
        var execLogs = await ctx.Set<ExecutionQualityLog>()
            .Include(e => e.Order)
            .Where(e => e.Symbol == strategy.Symbol && !e.IsDeleted && e.RecordedAt >= cutoff)
            .OrderBy(e => e.RecordedAt)
            .ToListAsync(cancellationToken);

        // Get average daily volume from candle data
        var dailyVolumes = await ctx.Set<Candle>()
            .Where(c => c.Symbol == strategy.Symbol && c.Timeframe == Timeframe.D1
                     && c.IsClosed && !c.IsDeleted && c.Timestamp >= cutoff)
            .Select(c => c.Volume)
            .ToListAsync(cancellationToken);

        decimal adv = dailyVolumes.Count > 0
            ? dailyVolumes.Average(v => (decimal)v)
            : 0;

        // Current aggregate lots on this strategy
        var currentLots = await ctx.Set<Position>()
            .Where(p => p.Symbol == strategy.Symbol && p.Status == PositionStatus.Open && !p.IsDeleted)
            .SumAsync(p => p.OpenLots, cancellationToken);

        // Build market impact curve from execution data.
        // Use the actual order quantity for meaningful regression — without it, the
        // power-law model would have identical X values making calibration meaningless.
        var impactPoints = new List<(decimal lots, decimal slippagePips)>();
        foreach (var log in execLogs)
        {
            var orderQty = log.Order?.Quantity ?? 0;
            if (log.SlippagePips != 0 && orderQty > 0)
            {
                impactPoints.Add((orderQty, Math.Abs(log.SlippagePips)));
            }
        }

        // Calibrate power-law model: slippage = k × lots^α
        decimal k = 0.1m; // Default: 0.1 pip per lot
        decimal alpha = 0.5m; // Default: square-root impact

        if (impactPoints.Count >= MinDataPoints)
        {
            // Simple log-linear regression: log(slippage) = log(k) + α × log(lots)
            var logLots = impactPoints.Select(p => Math.Log((double)p.lots)).ToArray();
            var logSlip = impactPoints.Select(p => Math.Log(Math.Max(0.0001, (double)p.slippagePips))).ToArray();

            var n = logLots.Length;
            var sumX = logLots.Sum();
            var sumY = logSlip.Sum();
            var sumXY = logLots.Zip(logSlip, (x, y) => x * y).Sum();
            var sumX2 = logLots.Sum(x => x * x);

            var denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) > 1e-10)
            {
                alpha = (decimal)((n * sumXY - sumX * sumY) / denom);
                k     = (decimal)Math.Exp((sumY - (double)alpha * sumX) / n);
                alpha  = Math.Clamp(alpha, 0.1m, 2.0m);
                k      = Math.Clamp(k, 0.001m, 10.0m);
            }
        }

        // Build impact curve for display (10 points from 0.1 to 10 lots)
        var curvePoints = new List<object>();
        for (decimal lots = 0.1m; lots <= 10.0m; lots += 1.0m)
        {
            var slip = k * (decimal)Math.Pow((double)lots, (double)alpha);
            curvePoints.Add(new { lots, expectedSlippagePips = Math.Round(slip, 4) });
        }

        // Capacity ceiling: find the lot size where slippage equals a threshold
        // Assume average alpha per trade ~ 2 pips (configurable)
        decimal alphaPerTrade = 2.0m;
        decimal capacityLots = alphaPerTrade > 0 && k > 0
            ? (decimal)Math.Pow((double)(alphaPerTrade / k), 1.0 / (double)alpha)
            : 100m;
        capacityLots = Math.Clamp(capacityLots, 0.01m, 1000m);

        var volumeParticipation = adv > 0 ? currentLots / adv * 100m : 0;
        var utilization = capacityLots > 0 ? currentLots / capacityLots * 100m : 0;
        var slippageAtCurrent = k * (decimal)Math.Pow(Math.Max(0.01, (double)currentLots), (double)alpha);

        var result = new StrategyCapacity
        {
            StrategyId                    = strategy.Id,
            Symbol                        = strategy.Symbol,
            AverageDailyVolume            = adv,
            VolumeParticipationRatePct    = volumeParticipation,
            CapacityCeilingLots           = Math.Round(capacityLots, 2),
            CurrentAggregateLots          = currentLots,
            UtilizationPct                = Math.Round(utilization, 2),
            MarketImpactCurveJson         = System.Text.Json.JsonSerializer.Serialize(curvePoints),
            EstimatedSlippageAtCurrentSize = Math.Round(slippageAtCurrent, 4),
            CalibrationWindowDays         = DefaultCalibrationDays,
            EstimatedAt                   = DateTime.UtcNow
        };

        _logger.LogDebug(
            "StrategyCapacity: {Symbol} strategy {Id} — capacity={Cap:F2} lots, utilization={Util:F1}%, " +
            "slippage@current={Slip:F4} pips",
            strategy.Symbol, strategy.Id, capacityLots, utilization, slippageAtCurrent);

        return result;
    }
}
