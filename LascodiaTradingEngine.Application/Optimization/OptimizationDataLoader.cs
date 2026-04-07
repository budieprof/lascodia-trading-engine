using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Encapsulates the data loading and preparation phase of the optimization pipeline.
/// Handles candle loading, regime-aware filtering with blend ratio, data quality
/// validation with holiday awareness, gap imputation, train/test splitting with
/// embargo, and transaction cost configuration from symbol metadata.
/// </summary>
[RegisterService(ServiceLifetime.Scoped)]
internal sealed class OptimizationDataLoader
{
    private readonly ILogger<OptimizationDataLoader> _logger;
    private readonly ISpreadProfileProvider _spreadProfileProvider;
    private readonly OptimizationValidator _validator;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationDataLoader(
        ILogger<OptimizationDataLoader> logger,
        ISpreadProfileProvider spreadProfileProvider,
        OptimizationValidator validator,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _spreadProfileProvider = spreadProfileProvider;
        _validator = validator;
        _timeProvider = timeProvider;
    }

    internal async Task<DataLoadResult> LoadAsync(
        DbContext db,
        OptimizationRun run,
        Strategy strategy,
        OptimizationConfig config,
        CancellationToken ct)
    {
        int effectiveLookbackMonths = config.CandleLookbackAutoScale
            ? ComputeEffectiveLookback(strategy.Timeframe, config.CandleLookbackMonths)
            : config.CandleLookbackMonths;
        var nowUtc = UtcNow;
        var candleLookbackStart = nowUtc.AddMonths(-effectiveLookbackMonths);

        var allCandles = await db.Set<Candle>()
            .Where(x => x.Symbol == strategy.Symbol
                     && x.Timeframe == strategy.Timeframe
                     && x.Timestamp >= candleLookbackStart
                     && x.Timestamp <= nowUtc
                     && x.IsClosed
                     && !x.IsDeleted)
            .OrderBy(x => x.Timestamp)
            .ToListAsync(ct);

        if (allCandles.Count == 0)
        {
            throw new DataQualityException(
                $"No candles found for {strategy.Symbol}/{strategy.Timeframe} in the last {effectiveLookbackMonths} months.");
        }

        var candles = await GetRegimeAwareCandlesAsync(
            db,
            _logger,
            strategy.Symbol,
            strategy.Timeframe,
            allCandles,
            ct,
            config.RegimeBlendRatio);

        var pairInfo = await db.Set<CurrencyPair>()
            .FirstOrDefaultAsync(p => p.Symbol == strategy.Symbol && !p.IsDeleted, ct);
        var strategyCurrencies = OptimizationRunMetadataService.ResolveStrategyCurrencies(strategy.Symbol, pairInfo);

        var holidayQuery = db.Set<EconomicEvent>()
            .Where(e => e.Impact == EconomicImpact.Holiday
                     && e.ScheduledAt >= candleLookbackStart
                     && e.ScheduledAt <= nowUtc
                     && !e.IsDeleted);
        if (strategyCurrencies.Count > 0)
            holidayQuery = holidayQuery.Where(e => strategyCurrencies.Contains(e.Currency));

        var holidayDates = await holidayQuery
            .Select(e => e.ScheduledAt.Date)
            .Distinct()
            .ToListAsync(ct);
        var holidaySet = new HashSet<DateTime>(holidayDates);

        var (imputedCandles, imputedCount) = OptimizationValidator.ImputeMinorGaps(
            candles,
            strategy.Timeframe,
            maxImputeBars: 2,
            holidayDates: holidaySet);
        if (imputedCount > 0)
        {
            _logger.LogDebug(
                "OptimizationDataLoader: imputed {Count} minor candle gap(s) for {Symbol}/{Tf}",
                imputedCount,
                strategy.Symbol,
                strategy.Timeframe);
            candles = imputedCandles;
        }

        var (dataValid, dataIssues) = OptimizationValidator.ValidateCandleQuality(
            candles,
            strategy.Timeframe,
            holidayDates: holidaySet,
            utcNow: nowUtc);
        if (!dataValid)
        {
            _logger.LogWarning(
                "OptimizationDataLoader: run {RunId} data quality check failed for {Symbol}/{Tf} - {Issues}",
                run.Id,
                strategy.Symbol,
                strategy.Timeframe,
                dataIssues);
            throw new DataQualityException(
                $"Data quality validation failed for {strategy.Symbol}/{strategy.Timeframe}: {dataIssues}");
        }

        var protocol = OptimizationGridBuilder.GetDataProtocol(candles.Count, config.DataScarcityThreshold);
        int splitIndex = (int)(candles.Count * protocol.TrainRatio);
        int embargoSize = Math.Max(1, (int)(candles.Count * config.EmbargoRatio));
        var trainCandles = candles.Take(splitIndex).ToList();
        var testCandles = candles.Skip(splitIndex + embargoSize).ToList();

        if (trainCandles.Count < 50)
        {
            throw new DataQualityException(
                $"Insufficient training candles ({trainCandles.Count}) for {strategy.Symbol}/{strategy.Timeframe}.");
        }

        if (testCandles.Count == 0)
        {
            throw new DataQualityException(
                $"No OOS candles after embargo for {strategy.Symbol}/{strategy.Timeframe} " +
                $"(total={candles.Count}, split={splitIndex}, embargo={embargoSize}).");
        }

        if (pairInfo is null || pairInfo.DecimalPlaces <= 0 || pairInfo.ContractSize <= 0)
        {
            throw new DataQualityException(
                $"Missing or invalid CurrencyPair metadata for {strategy.Symbol}. " +
                "Optimization requires DecimalPlaces and ContractSize before cost-aware validation can run.");
        }

        var pointSize = 1.0m / (decimal)Math.Pow(10, pairInfo.DecimalPlaces);
        double effectiveSpreadPoints = await ResolveEffectiveSpreadPointsAsync(
            db,
            strategy,
            pairInfo,
            config,
            nowUtc,
            ct);

        var screeningOptions = new BacktestOptions
        {
            SpreadPriceUnits = pointSize * (decimal)effectiveSpreadPoints,
            CommissionPerLot = (decimal)config.ScreeningCommissionPerLot,
            SlippagePriceUnits = pointSize * (decimal)config.ScreeningSlippagePips * 10,
            ContractSize = pairInfo.ContractSize,
        };

        await TryApplySpreadProfileAsync(strategy.Symbol, screeningOptions, ct);

        run.BaselineParametersJson = CanonicalParameterJson.Normalize(strategy.ParametersJson);
        string baselineParamsJson = CanonicalParameterJson.Normalize(strategy.ParametersJson);
        bool baselineRegimeParamsUsed = false;

        var currentRegimeForBaseline = await db.Set<MarketRegimeSnapshot>()
            .Where(s => s.Symbol == strategy.Symbol && s.Timeframe == strategy.Timeframe && !s.IsDeleted)
            .OrderByDescending(s => s.DetectedAt)
            .Select(s => (MarketRegimeEnum?)s.Regime)
            .FirstOrDefaultAsync(ct);

        if (currentRegimeForBaseline.HasValue)
        {
            var regimeParams = await db.Set<StrategyRegimeParams>()
                .Where(p => p.StrategyId == strategy.Id
                         && p.Regime == currentRegimeForBaseline.Value
                         && !p.IsDeleted)
                .Select(p => p.ParametersJson)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(regimeParams))
            {
                baselineParamsJson = CanonicalParameterJson.Normalize(regimeParams);
                baselineRegimeParamsUsed = true;
                _logger.LogDebug(
                    "OptimizationDataLoader: using regime-conditional params as baseline for {Symbol}/{Regime}",
                    strategy.Symbol,
                    currentRegimeForBaseline.Value);
            }
        }

        var baselineResult = await _validator.RunWithTimeoutAsync(
            strategy,
            baselineParamsJson,
            trainCandles,
            screeningOptions,
            config.ScreeningTimeoutSeconds,
            ct);
        run.BaselineHealthScore = OptimizationHealthScorer.ComputeHealthScore(baselineResult);
        run.BaselineParametersJson = baselineParamsJson;

        decimal baselineComparisonScore;
        if (testCandles.Count >= config.MinOosCandlesForValidation)
        {
            var baselineOosResult = await _validator.RunWithTimeoutAsync(
                strategy,
                baselineParamsJson,
                testCandles,
                screeningOptions,
                config.ScreeningTimeoutSeconds,
                ct);
            baselineComparisonScore = OptimizationHealthScorer.ComputeHealthScore(baselineOosResult);
        }
        else
        {
            baselineComparisonScore = (run.BaselineHealthScore.Value - protocol.ScorePenalty) * 0.85m;
        }

        return new DataLoadResult(
            strategy,
            candles,
            trainCandles,
            testCandles,
            embargoSize,
            screeningOptions,
            protocol,
            candleLookbackStart,
            currentRegimeForBaseline,
            baselineComparisonScore,
            baselineParamsJson,
            pairInfo,
            baselineRegimeParamsUsed);
    }

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

    private async Task<double> ResolveEffectiveSpreadPointsAsync(
        DbContext db,
        Strategy strategy,
        CurrencyPair pairInfo,
        OptimizationConfig config,
        DateTime nowUtc,
        CancellationToken ct)
    {
        double effectiveSpreadPoints = config.ScreeningSpreadPoints;
        if (!config.UseSymbolSpecificSpread || pairInfo.SpreadPoints <= 0)
            return effectiveSpreadPoints;

        double? p95Spread = null;
        var spreadCutoff = nowUtc.AddDays(-7);
        try
        {
            var p95Result = await db.Database.SqlQueryRaw<double>(
                    @"SELECT COALESCE(PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY ""SpreadPoints""), 0)
                      FROM ""TickRecords""
                      WHERE ""Symbol"" = {0}
                        AND ""IsDeleted"" = false
                        AND ""SpreadPoints"" > 0
                        AND ""TickTimestamp"" >= {1}
                      HAVING COUNT(*) >= 100",
                    strategy.Symbol,
                    spreadCutoff)
                .FirstOrDefaultAsync(ct);

            if (p95Result > 0)
                p95Spread = p95Result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "OptimizationDataLoader: native P95 spread query failed for {Symbol}, falling back to EF query",
                strategy.Symbol);

            try
            {
                var tickCount = await db.Set<TickRecord>()
                    .Where(tr => tr.Symbol == strategy.Symbol
                              && !tr.IsDeleted
                              && tr.SpreadPoints > 0
                              && tr.TickTimestamp >= spreadCutoff)
                    .CountAsync(ct);

                if (tickCount >= 100)
                {
                    int skipCount = (int)(tickCount * 0.95);
                    var p95Values = await db.Set<TickRecord>()
                        .Where(tr => tr.Symbol == strategy.Symbol
                                  && !tr.IsDeleted
                                  && tr.SpreadPoints > 0
                                  && tr.TickTimestamp >= spreadCutoff)
                        .OrderBy(tr => tr.SpreadPoints)
                        .Skip(skipCount)
                        .Take(1)
                        .Select(tr => tr.SpreadPoints)
                        .ToListAsync(ct);

                    if (p95Values.Count > 0)
                        p95Spread = (double)p95Values[0];
                }
            }
            catch (Exception innerEx)
            {
                _logger.LogDebug(
                    innerEx,
                    "OptimizationDataLoader: fallback P95 spread query also failed for {Symbol} (non-fatal)",
                    strategy.Symbol);
            }
        }

        if (p95Spread.HasValue && p95Spread.Value > 0)
        {
            effectiveSpreadPoints = Math.Max(config.ScreeningSpreadPoints, p95Spread.Value);
            _logger.LogDebug(
                "OptimizationDataLoader: using P95 spread for {Symbol}: {Spread:F1} points",
                strategy.Symbol,
                effectiveSpreadPoints);
        }
        else
        {
            effectiveSpreadPoints = Math.Max(config.ScreeningSpreadPoints, pairInfo.SpreadPoints * 1.5);
            _logger.LogDebug(
                "OptimizationDataLoader: using symbol-specific spread for {Symbol}: {Spread:F1} points (avg={Avg:F1}x1.5)",
                strategy.Symbol,
                effectiveSpreadPoints,
                pairInfo.SpreadPoints);
        }

        return effectiveSpreadPoints;
    }

    private async Task TryApplySpreadProfileAsync(
        string symbol,
        BacktestOptions screeningOptions,
        CancellationToken ct)
    {
        try
        {
            var profiles = await _spreadProfileProvider.GetProfilesAsync(symbol, ct);
            var spreadFunc = _spreadProfileProvider.BuildSpreadFunction(symbol, profiles);
            if (spreadFunc is not null)
                screeningOptions.SpreadFunction = spreadFunc;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OptimizationDataLoader: spread profile load failed for {Symbol} (non-fatal)", symbol);
        }
    }

    internal static async Task<List<Candle>> GetRegimeAwareCandlesAsync(
        DbContext db,
        ILogger logger,
        string symbol,
        Timeframe timeframe,
        List<Candle> allCandles,
        CancellationToken ct,
        double blendRatio = 0.20)
    {
        if (allCandles.Count < 100 || blendRatio >= 1.0)
            return allCandles;

        var latestRegime = await db.Set<MarketRegimeSnapshot>()
            .Where(s => s.Symbol == symbol && s.Timeframe == timeframe && !s.IsDeleted)
            .OrderByDescending(s => s.DetectedAt)
            .FirstOrDefaultAsync(ct);
        if (latestRegime is null)
            return allCandles;

        DateTime regimeStartedAt = latestRegime.DetectedAt;
        var previousDifferentAt = await db.Set<MarketRegimeSnapshot>()
            .Where(s => s.Symbol == symbol
                     && s.Timeframe == timeframe
                     && !s.IsDeleted
                     && s.DetectedAt < latestRegime.DetectedAt
                     && s.Regime != latestRegime.Regime)
            .OrderByDescending(s => s.DetectedAt)
            .Select(s => (DateTime?)s.DetectedAt)
            .FirstOrDefaultAsync(ct);

        var earliestCurrentInStreak = await db.Set<MarketRegimeSnapshot>()
            .Where(s => s.Symbol == symbol
                     && s.Timeframe == timeframe
                     && !s.IsDeleted
                     && s.Regime == latestRegime.Regime
                     && (!previousDifferentAt.HasValue || s.DetectedAt > previousDifferentAt.Value))
            .OrderBy(s => s.DetectedAt)
            .Select(s => (DateTime?)s.DetectedAt)
            .FirstOrDefaultAsync(ct);

        if (earliestCurrentInStreak.HasValue)
            regimeStartedAt = earliestCurrentInStreak.Value;

        var regimeCandles = allCandles.Where(c => c.Timestamp >= regimeStartedAt).ToList();
        if (regimeCandles.Count < 100)
        {
            logger.LogDebug(
                "OptimizationDataLoader: regime segment too short ({Count} bars) for {Symbol} - using full candle window",
                regimeCandles.Count,
                symbol);
            return allCandles;
        }

        if (blendRatio <= 0.0 || blendRatio >= 1.0)
        {
            logger.LogDebug(
                "OptimizationDataLoader: using regime-filtered candles for {Symbol} ({Regime}, {Count} bars from {Start:d})",
                symbol,
                latestRegime.Regime,
                regimeCandles.Count,
                regimeStartedAt);
            return regimeCandles;
        }

        var nonRegimeCandles = allCandles.Where(c => c.Timestamp < regimeStartedAt).ToList();
        int blendCount = (int)(regimeCandles.Count * blendRatio / (1.0 - blendRatio));
        blendCount = Math.Min(blendCount, nonRegimeCandles.Count);
        if (blendCount <= 0)
            return regimeCandles;

        var indices = Enumerable.Range(0, nonRegimeCandles.Count).ToArray();
        var rng = new DeterministicRandom(nonRegimeCandles.Count ^ blendCount);
        for (int i = 0; i < Math.Min(blendCount, indices.Length); i++)
        {
            int j = i + rng.Next(indices.Length - i);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        var sampled = indices
            .Take(blendCount)
            .Order()
            .Select(i => nonRegimeCandles[i])
            .ToList();

        var blended = sampled.Concat(regimeCandles)
            .OrderBy(c => c.Timestamp)
            .ToList();

        logger.LogDebug(
            "OptimizationDataLoader: using blended regime candles for {Symbol} ({Regime}, {RegimeCount} regime + {BlendCount} non-regime bars)",
            symbol,
            latestRegime.Regime,
            regimeCandles.Count,
            sampled.Count);
        return blended;
    }
}
