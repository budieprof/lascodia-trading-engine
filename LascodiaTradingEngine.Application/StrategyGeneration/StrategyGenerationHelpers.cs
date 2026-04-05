using System.Text.Json;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Pure static helper methods used by <see cref="StrategyGenerationWorker"/> and
/// <see cref="StrategyScreeningEngine"/>. This class has no DI dependencies, no state,
/// and no side effects — every method is a deterministic function of its arguments,
/// making it trivially testable without mocking.
///
/// <b>Responsibilities:</b>
/// <list type="bullet">
///   <item>Asset classification (FX major/minor/exotic, indices, commodities, crypto).</item>
///   <item>Market calendar: weekend guard (crypto-exempt), blackout periods with timezone support.</item>
///   <item>Timeframe scaling: screening window, train/test split ratio, min-trade adjustment.</item>
///   <item>Regime logic: threshold scaling, counter-regime types, MTF compatibility, template ordering.</item>
///   <item>ATR computation, spread/cost building, parameter injection.</item>
///   <item>Adaptive threshold helpers: multiplier computation, median, recency-weighted survival.</item>
///   <item>Config parsing (timeframes).</item>
/// </list>
///
/// <b>Not here:</b> Anything that needs a backtest engine, DB context, logger, or DI scope
/// belongs in <see cref="StrategyScreeningEngine"/> or <see cref="StrategyGenerationWorker"/>.
/// </summary>
public static class StrategyGenerationHelpers
{
    /// <summary>Asset class for multi-asset screening parameter adjustments.</summary>
    public enum AssetClass { FxMajor, FxMinor, FxExotic, Index, Commodity, Crypto, Unknown }

    /// <summary>
    /// Returns per-metric threshold multipliers for the given asset class.
    /// Applied in BuildThresholdsForTimeframe to tighten thresholds for asset classes
    /// where transaction costs erode thin edges (exotics, crypto) or where trending
    /// behavior makes lower win rates acceptable (indices, commodities).
    /// </summary>
    public static (double WinRate, double ProfitFactor, double Sharpe, double Drawdown)
        GetAssetClassThresholdMultipliers(AssetClass assetClass) => assetClass switch
    {
        AssetClass.FxMajor   => (1.00, 1.00, 1.00, 1.00),  // Baseline — tight spreads, high liquidity
        AssetClass.FxMinor   => (1.00, 1.05, 1.05, 0.95),   // Slightly wider costs
        AssetClass.FxExotic  => (0.95, 1.20, 1.30, 0.85),   // High costs — demand more PF/Sharpe, relax WR/DD
        AssetClass.Index     => (0.92, 1.05, 1.00, 0.90),   // Trending — relax WR, slightly tighter PF
        AssetClass.Commodity => (0.92, 1.10, 1.10, 0.90),   // Volatile — demand more PF/Sharpe
        AssetClass.Crypto    => (0.90, 1.20, 1.20, 0.85),   // 24/7 high vol — relax WR/DD, demand PF/Sharpe
        _                    => (1.00, 1.00, 1.00, 1.00),   // Unknown — baseline
    };

    // ── Asset classification (#24: extensible) ─────────────────────────────

    private static readonly HashSet<string> FxMajors = new(StringComparer.OrdinalIgnoreCase)
    {
        "EURUSD", "GBPUSD", "USDJPY", "USDCHF", "AUDUSD", "NZDUSD", "USDCAD"
    };

    private static readonly HashSet<string> FxMinors = new(StringComparer.OrdinalIgnoreCase)
    {
        "EURGBP", "EURJPY", "GBPJPY", "EURAUD", "AUDNZD", "AUDCAD", "GBPAUD",
        "NZDJPY", "CADJPY", "CHFJPY", "EURNZD", "EURCAD", "GBPNZD", "GBPCAD"
    };

    private static readonly HashSet<string> Indices = new(StringComparer.OrdinalIgnoreCase)
    {
        "US30", "US500", "NAS100", "GER40", "UK100", "JPN225", "AUS200", "FRA40",
        "SPX500", "DJ30", "USTEC", "DE30", "DE40"
    };

    private static readonly HashSet<string> Commodities = new(StringComparer.OrdinalIgnoreCase)
    {
        "XAUUSD", "XAGUSD", "XAUEUR", "WTIUSD", "BRENTUSD", "XNGUSD",
        "GOLD", "SILVER", "OIL", "USOIL", "UKOIL"
    };

    private static readonly HashSet<string> Cryptos = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTCUSD", "ETHUSD", "LTCUSD", "XRPUSD", "BCHUSD",
        "BTCEUR", "ETHEUR", "BTCGBP"
    };

    /// <summary>
    /// Classifies a symbol into an asset class. First checks CurrencyPair metadata
    /// (if available), then falls back to hardcoded symbol lists and heuristics (#24).
    /// </summary>
    public static AssetClass ClassifyAsset(string symbol, CurrencyPair? pairInfo)
    {
        var upper = symbol.ToUpperInvariant();
        if (FxMajors.Contains(upper)) return AssetClass.FxMajor;
        if (FxMinors.Contains(upper)) return AssetClass.FxMinor;
        if (Indices.Contains(upper)) return AssetClass.Index;
        if (Commodities.Contains(upper)) return AssetClass.Commodity;
        if (Cryptos.Contains(upper)) return AssetClass.Crypto;
        if (upper.Length == 6 && pairInfo is { BaseCurrency.Length: 3, QuoteCurrency.Length: 3 })
            return AssetClass.FxExotic;
        return AssetClass.Unknown;
    }

    // ── Market calendar awareness (#7, #14) ────────────────────────────────

    /// <summary>
    /// Returns true if generation should be skipped due to weekend/market closure.
    /// Crypto assets trade 24/7 and are excluded from the weekend check (#7).
    /// </summary>
    public static bool IsWeekendForAssetMix(IEnumerable<(string Symbol, CurrencyPair? Pair)> symbols)
    {
        var now = DateTime.UtcNow;
        if (now.DayOfWeek != DayOfWeek.Saturday && now.DayOfWeek != DayOfWeek.Sunday)
            return false;

        // If ALL symbols are crypto, allow weekend generation
        bool hasFx = symbols.Any(s => ClassifyAsset(s.Symbol, s.Pair) != AssetClass.Crypto);
        return hasFx;
    }

    /// <summary>
    /// Returns true if the current date/time falls within any configured blackout period.
    /// Supports optional timezone configuration (#15).
    /// </summary>
    public static bool IsInBlackoutPeriod(string blackoutPeriods, string blackoutTimezone = "UTC")
    {
        if (string.IsNullOrWhiteSpace(blackoutPeriods)) return false;

        DateTime now;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(blackoutTimezone);
            now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch
        {
            now = DateTime.UtcNow;
        }

        int todayOrdinal = now.Month * 100 + now.Day;

        foreach (var period in blackoutPeriods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = period.Split('-');
            if (parts.Length != 2) continue;
            if (!TryParseMonthDay(parts[0], out int startOrdinal)) continue;
            if (!TryParseMonthDay(parts[1], out int endOrdinal)) continue;

            if (startOrdinal <= endOrdinal)
            {
                if (todayOrdinal >= startOrdinal && todayOrdinal <= endOrdinal) return true;
            }
            else
            {
                if (todayOrdinal >= startOrdinal || todayOrdinal <= endOrdinal) return true;
            }
        }

        return false;

        static bool TryParseMonthDay(string s, out int ordinal)
        {
            ordinal = 0;
            var md = s.Split('/');
            if (md.Length != 2) return false;
            if (!int.TryParse(md[0], out int month) || !int.TryParse(md[1], out int day)) return false;
            if (month < 1 || month > 12 || day < 1 || day > 31) return false;
            ordinal = month * 100 + day;
            return true;
        }
    }

    // ── Timeframe scaling ──────────────────────────────────────────────────

    public static double GetTrainSplitRatio(int candleCount) => candleCount switch
    {
        >= 1000 => 0.70,
        >= 500  => 0.75,
        _       => 0.80,
    };

    public static int AdjustMinTradesForTimeframe(int baseTrades, Timeframe tf) => tf switch
    {
        Timeframe.M1 or Timeframe.M5 or Timeframe.M15 => baseTrades,
        Timeframe.H1  => Math.Max(5, (int)(baseTrades * 0.8)),
        Timeframe.H4  => Math.Max(5, (int)(baseTrades * 0.5)),
        Timeframe.D1  => Math.Max(3, (int)(baseTrades * 0.3)),
        _             => baseTrades,
    };

    public static int ScaleScreeningWindowForTimeframe(int baseMonths, Timeframe tf) => tf switch
    {
        Timeframe.M1 or Timeframe.M5  => Math.Max(1, baseMonths / 2),
        Timeframe.M15                 => baseMonths,
        Timeframe.H1                  => baseMonths,
        Timeframe.H4                  => (int)(baseMonths * 1.5),
        Timeframe.D1                  => baseMonths * 3,
        _                             => baseMonths,
    };

    public static Timeframe? GetHigherTimeframe(Timeframe tf) => tf switch
    {
        Timeframe.M1  => Timeframe.M5,
        Timeframe.M5  => Timeframe.M15,
        Timeframe.M15 => Timeframe.H1,
        Timeframe.H1  => Timeframe.H4,
        Timeframe.H4  => Timeframe.D1,
        _             => null,
    };

    // ── Regime logic ───────────────────────────────────────────────────────

    public static bool IsRegimeCompatibleWithStrategy(StrategyType strategyType, MarketRegimeEnum higherTfRegime)
    {
        if (strategyType is StrategyType.MovingAverageCrossover or StrategyType.MACDDivergence or StrategyType.MomentumTrend)
            return higherTfRegime is MarketRegimeEnum.Trending or MarketRegimeEnum.Breakout;

        if (strategyType is StrategyType.RSIReversion or StrategyType.BollingerBandReversion)
            return higherTfRegime is MarketRegimeEnum.Ranging or MarketRegimeEnum.LowVolatility;

        if (strategyType is StrategyType.BreakoutScalper or StrategyType.SessionBreakout)
            return higherTfRegime is MarketRegimeEnum.Trending or MarketRegimeEnum.HighVolatility or MarketRegimeEnum.Breakout;

        if (strategyType is StrategyType.StatisticalArbitrage or StrategyType.VwapReversion)
            return higherTfRegime is MarketRegimeEnum.Ranging or MarketRegimeEnum.LowVolatility;

        return true; // Unknown — don't block
    }

    /// <summary>
    /// Scales screening thresholds based on market regime. Breakout/high-vol regimes get relaxed
    /// win rate and tighter drawdown; ranging/low-vol get tighter win rate.
    /// </summary>
    public static (double MinWinRate, double MinPF, double MinSharpe, double MaxDD) ScaleThresholdsForRegime(
        double baseWinRate, double basePF, double baseSharpe, double baseMaxDD, MarketRegimeEnum regime) => regime switch
    {
        MarketRegimeEnum.HighVolatility or MarketRegimeEnum.Breakout =>
            (baseWinRate * 0.85, basePF, baseSharpe, baseMaxDD * 0.85),
        MarketRegimeEnum.Ranging or MarketRegimeEnum.LowVolatility =>
            (Math.Min(baseWinRate * 1.10, 0.80), basePF * 1.05, baseSharpe, baseMaxDD * 1.15),
        MarketRegimeEnum.Trending =>
            (baseWinRate * 0.95, basePF, baseSharpe * 0.95, baseMaxDD),
        _ => (baseWinRate, basePF, baseSharpe, baseMaxDD),
    };

    public static StrategyType[] GetCounterRegimeTypes(MarketRegimeEnum currentRegime) => currentRegime switch
    {
        MarketRegimeEnum.Trending or MarketRegimeEnum.Breakout => [StrategyType.RSIReversion, StrategyType.BollingerBandReversion],
        MarketRegimeEnum.Ranging or MarketRegimeEnum.LowVolatility => [StrategyType.MovingAverageCrossover, StrategyType.BreakoutScalper],
        MarketRegimeEnum.HighVolatility => [StrategyType.RSIReversion, StrategyType.BollingerBandReversion],
        _ => [],
    };

    // ── Template ordering ──────────────────────────────────────────────────

    public static IReadOnlyList<string> OrderTemplatesForRegime(
        IReadOnlyList<string> templates, MarketRegimeEnum regime,
        IReadOnlyDictionary<string, double>? templateSurvivalRates = null)
    {
        if (templates.Count <= 1) return templates;

        // When survival rate data is available, partition into known vs unknown groups
        if (templateSurvivalRates is { Count: > 0 })
        {
            var withData = new List<(string Template, double SurvivalRate)>();
            var withoutData = new List<string>();

            foreach (var t in templates)
            {
                if (templateSurvivalRates.TryGetValue(t, out var rate))
                    withData.Add((t, rate));
                else
                    withoutData.Add(t);
            }

            // Known templates ordered by descending survival rate
            var sorted = withData
                .OrderByDescending(x => x.SurvivalRate)
                .Select(x => x.Template)
                .ToList();

            // Unknown templates fall back to existing regime-based ordering
            sorted.AddRange(OrderTemplatesByRegimeLogic(withoutData, regime));
            return sorted;
        }

        return OrderTemplatesByRegimeLogic(templates, regime);
    }

    private static IReadOnlyList<string> OrderTemplatesByRegimeLogic(IReadOnlyList<string> templates, MarketRegimeEnum regime)
    {
        if (templates.Count <= 1) return templates;

        if (regime is MarketRegimeEnum.HighVolatility or MarketRegimeEnum.Breakout)
        {
            return templates
                .Select(t => (Template: t, Score: ComputeTemplateParameterSum(t)))
                .OrderByDescending(x => x.Score)
                .Select(x => x.Template).ToList();
        }

        if (regime is MarketRegimeEnum.Ranging or MarketRegimeEnum.LowVolatility)
        {
            return templates
                .Select(t => (Template: t, Score: ComputeTemplateParameterSum(t)))
                .OrderBy(x => x.Score)
                .Select(x => x.Template).ToList();
        }

        return templates;
    }

    private static double ComputeTemplateParameterSum(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            double sum = 0;
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var val))
                    sum += val;
            return sum;
        }
        catch { return 0; }
    }

    // ── ATR & spread ───────────────────────────────────────────────────────

    public static decimal ComputeAtr(List<Candle> candles, int period = 14)
    {
        if (candles.Count < period + 1) return 0m;
        decimal atr = 0m;
        for (int i = candles.Count - period; i < candles.Count; i++)
        {
            var c = candles[i];
            var prev = candles[i - 1];
            var tr = Math.Max(c.High - c.Low,
                     Math.Max(Math.Abs(c.High - prev.Close), Math.Abs(c.Low - prev.Close)));
            atr += tr;
        }
        return atr / period;
    }

    public static string InjectAtrContext(string parametersJson, decimal atr)
    {
        if (string.IsNullOrWhiteSpace(parametersJson) || atr <= 0m) return parametersJson;
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            using var ms = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    prop.WriteTo(writer);
                writer.WriteNumber("ScreeningAtr", atr);
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return parametersJson; }
    }

    /// <summary>Max age of live price data before it's considered stale for spread estimation.</summary>
    private static readonly TimeSpan LivePriceStalenessLimit = TimeSpan.FromHours(2);

    public static BacktestOptions BuildScreeningOptions(
        string symbol, CurrencyPair? pairInfo, AssetClass assetClass,
        double screeningSpreadPoints, double screeningCommissionPerLot,
        double screeningSlippagePips, ILivePriceCache livePriceCache)
    {
        var pointSize = pairInfo != null && pairInfo.DecimalPlaces > 0
            ? 1.0m / (decimal)Math.Pow(10, pairInfo.DecimalPlaces)
            : GetDefaultPointSize(assetClass);

        decimal spreadPriceUnits = pointSize * (decimal)screeningSpreadPoints;

        var livePrice = livePriceCache.Get(symbol);
        if (livePrice.HasValue && livePrice.Value.Ask > livePrice.Value.Bid
            && (DateTime.UtcNow - livePrice.Value.Timestamp) < LivePriceStalenessLimit)
        {
            var liveSpread = livePrice.Value.Ask - livePrice.Value.Bid;
            if (liveSpread > spreadPriceUnits) spreadPriceUnits = liveSpread;
        }

        decimal commissionPerLot = ScaleCommissionForAssetClass((decimal)screeningCommissionPerLot, assetClass);
        if (pairInfo != null && pairInfo.PipSize > 0)
        {
            decimal pipSizeRatio = pairInfo.PipSize / 10m;
            if (pipSizeRatio > 1.5m) commissionPerLot *= pipSizeRatio;
        }

        return new BacktestOptions
        {
            SpreadPriceUnits   = spreadPriceUnits,
            CommissionPerLot   = commissionPerLot,
            SlippagePriceUnits = pointSize * (decimal)screeningSlippagePips * 10,
            ContractSize       = pairInfo?.ContractSize ?? GetDefaultContractSize(assetClass),
        };
    }

    public static decimal GetDefaultPointSize(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Index     => 0.01m,
        AssetClass.Commodity => 0.01m,
        AssetClass.Crypto    => 0.01m,
        _                    => 0.00001m,
    };

    public static decimal GetDefaultContractSize(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Index     => 1m,
        AssetClass.Commodity => 100m,
        AssetClass.Crypto    => 1m,
        _                    => 100_000m,
    };

    public static decimal ScaleCommissionForAssetClass(decimal baseCommission, AssetClass assetClass) => assetClass switch
    {
        AssetClass.Index     => baseCommission * 0.5m,
        AssetClass.Commodity => baseCommission * 1.5m,
        AssetClass.Crypto    => baseCommission * 2.0m,
        AssetClass.FxExotic  => baseCommission * 1.5m,
        _                    => baseCommission,
    };

    public static double GetSpreadToRangeLimit(AssetClass assetClass, double baseLimit) => assetClass switch
    {
        AssetClass.Index     => baseLimit * 1.5,
        AssetClass.Commodity => baseLimit * 1.3,
        AssetClass.Crypto    => baseLimit * 2.0,
        AssetClass.FxExotic  => baseLimit * 1.3,
        _                    => baseLimit,
    };

    // ── Config parsing ─────────────────────────────────────────────────────

    public static List<Timeframe> ParseTimeframes(string config)
    {
        var result = new List<Timeframe>();
        foreach (var part in config.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Enum.TryParse<Timeframe>(part, ignoreCase: true, out var tf))
                result.Add(tf);
        return result.Count > 0 ? result : [Timeframe.H1, Timeframe.H4];
    }

    // ── Adaptive threshold helpers (#11, #12, #23) ─────────────────────────

    public static double ComputeAdaptiveMultiplier(double observedMedian, double baseThreshold)
    {
        if (baseThreshold <= 0) return 1.0;
        double ratio = observedMedian / baseThreshold;
        return Math.Clamp(ratio, 0.85, 1.25);
    }

    public static double ApplyAdaptiveAdjustment(double threshold, double multiplier)
        => threshold * multiplier;

    public static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    /// <summary>
    /// Computes recency-weighted survival rate for performance feedback (#12).
    /// More recent strategies get exponentially higher weight.
    /// The <paramref name="halfLifeDays"/> parameter controls how fast old data decays —
    /// shorter half-life weights recent data more heavily. Adjusted by
    /// <see cref="IFeedbackDecayMonitor"/> based on prediction accuracy.
    /// </summary>
    public static double ComputeRecencyWeightedSurvivalRate(
        IEnumerable<(bool Survived, DateTime CreatedAt)> strategies,
        double halfLifeDays = 62.0)
    {
        double weightedSurvived = 0, totalWeight = 0;
        var now = DateTime.UtcNow;
        double denominator = halfLifeDays / Math.Log(2); // halfLifeDays=62 → ~89.4 ≈ previous 90.0

        foreach (var (survived, createdAt) in strategies)
        {
            double daysAgo = (now - createdAt).TotalDays;
            double weight = Math.Exp(-daysAgo / denominator);
            totalWeight += weight;
            if (survived) weightedSurvived += weight;
        }

        return totalWeight > 0 ? weightedSurvived / totalWeight : 0;
    }

    // ── Regime transition & multi-timeframe helpers (#6, #7, #8, #10) ──────

    /// <summary>
    /// Strategy types that perform well during regime transitions (volatility
    /// expansion during shift). Used to allocate a hedge slot when the regime
    /// detector signals an imminent transition (#6).
    /// </summary>
    public static IReadOnlyList<StrategyType> GetTransitionTypes()
        => new[] { StrategyType.BreakoutScalper, StrategyType.MomentumTrend, StrategyType.SessionBreakout };

    /// <summary>
    /// Computes a confidence multiplier based on multi-timeframe regime consensus.
    /// When the higher timeframe agrees with the primary, confidence is boosted;
    /// disagreement applies a penalty; missing data is neutral (#7).
    /// </summary>
    public static double ComputeMultiTimeframeConfidenceBoost(
        MarketRegimeEnum primaryRegime, string symbol, Timeframe primaryTf,
        IReadOnlyDictionary<(string, Timeframe), MarketRegimeEnum> regimeBySymbolTf)
    {
        var higherTf = GetHigherTimeframe(primaryTf);
        if (higherTf is null)
            return 1.0;

        if (regimeBySymbolTf.TryGetValue((symbol, higherTf.Value), out var higherRegime))
            return higherRegime == primaryRegime ? 1.15 : 0.90;

        return 1.0;
    }

    /// <summary>
    /// Scales template count / exploration based on how long the current regime
    /// has been active. Very young regimes reduce exploration (may be noise);
    /// mature regimes slightly boost it (#8).
    /// </summary>
    public static double ComputeRegimeDurationFactor(DateTime regimeDetectedAt)
    {
        var elapsed = DateTime.UtcNow - regimeDetectedAt;
        if (elapsed.TotalDays < 2) return 0.8;
        if (elapsed.TotalDays <= 14) return 1.0;
        return 1.1;
    }

    /// <summary>
    /// Builds a composite feedback key that includes the timeframe dimension,
    /// so performance feedback is tracked per (StrategyType, Regime, Timeframe)
    /// rather than just (StrategyType, Regime) (#10).
    /// </summary>
    public static (StrategyType, MarketRegimeEnum, Timeframe) MakeFeedbackKey(
        StrategyType type, MarketRegimeEnum regime, Timeframe timeframe)
        => (type, regime, timeframe);
}
