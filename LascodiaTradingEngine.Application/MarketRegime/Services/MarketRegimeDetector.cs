using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.MarketRegime.Services;

/// <summary>
/// Hybrid market regime detector that combines a rule-based classifier (ADX/ATR/BBW)
/// with a Hidden Markov Model (<see cref="HmmRegimeDetector"/>) via weighted voting.
///
/// <list type="bullet">
///   <item><description>Rule-based logic uses hardcoded ADX, ATR, and Bollinger Band Width thresholds.</description></item>
///   <item><description>HMM learns Gaussian emission distributions from the provided candle window.</description></item>
///   <item><description>Final regime = weighted combination of both; configurable via EngineConfig keys.</description></item>
///   <item><description>Transition smoothing prevents noisy regime flips.</description></item>
/// </list>
///
/// EngineConfig keys:
/// <list type="bullet">
///   <item><description><c>RegimeDetector:RuleWeight</c> — weight for rule-based confidence (default 0.6).</description></item>
///   <item><description><c>RegimeDetector:HmmWeight</c> — weight for HMM confidence (default 0.4).</description></item>
///   <item><description><c>RegimeDetector:TransitionMinConfidence</c> — minimum confidence to accept a regime change (default 0.6).</description></item>
/// </list>
/// </summary>
public class MarketRegimeDetector : IMarketRegimeDetector
{
    private const int AdxPeriod = 14;
    private const int AtrPeriod = 14;
    private const int BbPeriod  = 20;

    // ── Config key constants ──────────────────────────────────────────────────

    private const string CK_RuleWeight             = "RegimeDetector:RuleWeight";
    private const string CK_HmmWeight              = "RegimeDetector:HmmWeight";
    private const string CK_TransitionMinConfidence = "RegimeDetector:TransitionMinConfidence";

    // ── Default hybrid weights ────────────────────────────────────────────────

    private const double DefaultRuleWeight             = 0.6;
    private const double DefaultHmmWeight              = 0.4;
    private const double DefaultTransitionMinConfidence = 0.6;

    /// <summary>
    /// When the rule-based and HMM detectors disagree on regime, the winning
    /// confidence is dampened by this factor to reflect the uncertainty.
    /// </summary>
    private const double DisagreementDampeningFactor = 0.85;

    /// <summary>Tracks the previous detection result for transition smoothing.</summary>
    private MarketRegimeEnum? _previousRegime;

    /// <summary>The HMM component used alongside the rule-based classifier.</summary>
    private readonly HmmRegimeDetector _hmm = new();

    /// <summary>
    /// Optional read DB context for loading EngineConfig overrides.
    /// When null (e.g. in unit tests), default weights are used.
    /// </summary>
    private readonly IReadApplicationDbContext? _readDbContext;

    /// <summary>
    /// Creates a hybrid detector without database access (uses default weights).
    /// </summary>
    public MarketRegimeDetector()
    {
    }

    /// <summary>
    /// Creates a hybrid detector that reads weight overrides from EngineConfig.
    /// </summary>
    public MarketRegimeDetector(IReadApplicationDbContext readDbContext)
    {
        _readDbContext = readDbContext;
    }

    public async Task<MarketRegimeSnapshot> DetectAsync(
        string symbol,
        Timeframe timeframe,
        IReadOnlyList<Candle> candles,
        CancellationToken ct)
    {
        if (candles.Count < Math.Max(AdxPeriod + 1, BbPeriod))
            throw new InvalidOperationException(
                $"Insufficient candle data for regime detection. Need at least {Math.Max(AdxPeriod + 1, BbPeriod)} candles, got {candles.Count}.");

        // ── Load configurable weights from EngineConfig ───────────────────
        double ruleWeight             = DefaultRuleWeight;
        double hmmWeight              = DefaultHmmWeight;
        double transitionMinConfidence = DefaultTransitionMinConfidence;

        if (_readDbContext is DbContext dbCtx)
        {
            ruleWeight             = await GetConfigAsync<double>(dbCtx, CK_RuleWeight, DefaultRuleWeight, ct);
            hmmWeight              = await GetConfigAsync<double>(dbCtx, CK_HmmWeight, DefaultHmmWeight, ct);
            transitionMinConfidence = await GetConfigAsync<double>(dbCtx, CK_TransitionMinConfidence, DefaultTransitionMinConfidence, ct);
        }

        // Normalize weights so they sum to 1
        double weightSum = ruleWeight + hmmWeight;
        if (weightSum > 0)
        {
            ruleWeight /= weightSum;
            hmmWeight  /= weightSum;
        }

        // ── 1. Run rule-based detection (all original logic preserved) ────
        var (ruleRegime, ruleConfidence, adx, atr, bbw) = RunRuleBasedDetection(candles);

        // ── 2. Run HMM detection ──────────────────────────────────────────
        var (hmmRegime, hmmConfidence) = _hmm.Detect(candles, symbol, timeframe);

        // ── 3. Combine via weighted voting ────────────────────────────────
        MarketRegimeEnum finalRegime;
        double finalConfidence;

        if (ruleRegime == hmmRegime)
        {
            // Both agree — weighted average of confidences
            finalRegime     = ruleRegime;
            finalConfidence = ruleWeight * ruleConfidence + hmmWeight * hmmConfidence;
        }
        else
        {
            // Disagreement — pick the one with higher confidence, apply dampening
            double ruleWeightedConf = ruleWeight * ruleConfidence;
            double hmmWeightedConf  = hmmWeight * hmmConfidence;

            if (ruleWeightedConf >= hmmWeightedConf)
            {
                finalRegime     = ruleRegime;
                finalConfidence = ruleWeightedConf * DisagreementDampeningFactor;
            }
            else
            {
                finalRegime     = hmmRegime;
                finalConfidence = hmmWeightedConf * DisagreementDampeningFactor;
            }
        }

        // ── 4. Transition smoothing ───────────────────────────────────────
        if (_previousRegime.HasValue && finalRegime != _previousRegime.Value)
        {
            if (finalConfidence < transitionMinConfidence)
            {
                // Confidence too low to justify the regime change — keep previous
                finalRegime = _previousRegime.Value;
            }
        }

        _previousRegime = finalRegime;

        // Clamp confidence to [0, 1]
        finalConfidence = Math.Clamp(finalConfidence, 0.0, 1.0);

        var snapshot = new MarketRegimeSnapshot
        {
            Symbol             = symbol.ToUpperInvariant(),
            Timeframe          = timeframe,
            Regime             = finalRegime,
            Confidence         = (decimal)Math.Round(finalConfidence, 4),
            ADX                = (decimal)Math.Round(adx, 4),
            ATR                = (decimal)Math.Round(atr, 6),
            BollingerBandWidth = (decimal)Math.Round(bbw, 6),
            DetectedAt         = DateTime.UtcNow
        };

        return snapshot;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  RULE-BASED DETECTION (original logic, fully preserved)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes the original rule-based regime classification using ADX, ATR,
    /// and Bollinger Band Width. Returns the regime, confidence, and indicator values.
    /// </summary>
    private static (MarketRegimeEnum Regime, double Confidence, double Adx, double Atr, double Bbw)
        RunRuleBasedDetection(IReadOnlyList<Candle> candles)
    {
        double adx = CalculateAdx(candles);
        double atr = CalculateAtr(candles, AtrPeriod);
        double bbw = CalculateBollingerBandWidth(candles, BbPeriod);

        // ATR moving average over last 20 periods for high/low volatility classification
        double atrAvg = CalculateAtrAverage(candles, AtrPeriod, 20);

        MarketRegimeEnum regime;
        double confidence;

        // ── Crisis: extreme ATR spike with directional sell-off ─────────────
        bool isCrisis = atrAvg > 0 && atr > atrAvg * 2.5 && HasTrailingSellOff(candles, 3);

        // ── Breakout: BB compression (pre-expansion) + current candle expansion ──
        bool isBreakout = false;
        if (!isCrisis && candles.Count > BbPeriod)
        {
            var preExpansion = candles.Take(candles.Count - 1).ToList();
            double bbwPre = CalculateBollingerBandWidth(preExpansion, BbPeriod);
            double bbwAvg = CalculateBbwRollingAverage(preExpansion, BbPeriod, 20);
            if (bbwAvg > 0 && bbwPre < bbwAvg * 0.4)
            {
                double latestTr = (double)(candles[^1].High - candles[^1].Low);
                if (atr > 0 && latestTr > atr * 1.8)
                    isBreakout = true;
            }
        }

        if (isCrisis)
        {
            regime     = MarketRegimeEnum.Crisis;
            confidence = Math.Min(1.0, atr / (atrAvg * 3.0));
        }
        else if (isBreakout)
        {
            regime     = MarketRegimeEnum.Breakout;
            confidence = Math.Min(1.0, (double)(candles[^1].High - candles[^1].Low) / atr);
        }
        else if (atr > atrAvg * 1.5)
        {
            regime     = MarketRegimeEnum.HighVolatility;
            confidence = Math.Min(1.0, atr / (atrAvg * 2.0));
        }
        else if (atr < atrAvg * 0.5)
        {
            regime     = MarketRegimeEnum.LowVolatility;
            confidence = Math.Min(1.0, (atrAvg * 0.5 - atr) / (atrAvg * 0.5));
        }
        else if (adx > 25.0)
        {
            regime     = MarketRegimeEnum.Trending;
            confidence = Math.Min(1.0, adx / 50.0);
        }
        else
        {
            regime     = MarketRegimeEnum.Ranging;
            confidence = 1.0 - Math.Min(1.0, adx / 50.0);
        }

        return (regime, confidence, adx, atr, bbw);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  INDICATOR CALCULATIONS (original, unchanged)
    // ══════════════════════════════════════════════════════════════════════════

    // ── ADX (Average Directional Index) ───────────────────────────────────────

    private static double CalculateAdx(IReadOnlyList<Candle> candles)
    {
        int n = candles.Count;
        if (n < AdxPeriod + 1) return 0.0;

        // True Range
        var trList  = new double[n];
        var dmPlus  = new double[n];
        var dmMinus = new double[n];

        for (int i = 1; i < n; i++)
        {
            double high  = (double)candles[i].High;
            double low   = (double)candles[i].Low;
            double close = (double)candles[i - 1].Close;

            double tr = Math.Max(high - low, Math.Max(Math.Abs(high - close), Math.Abs(low - close)));
            trList[i] = tr;

            double upMove   = high - (double)candles[i - 1].High;
            double downMove = (double)candles[i - 1].Low - low;

            dmPlus[i]  = (upMove > downMove   && upMove > 0)   ? upMove   : 0.0;
            dmMinus[i] = (downMove > upMove   && downMove > 0) ? downMove : 0.0;
        }

        // Wilder smoothing for first ATR period
        double smoothedTr     = trList.Skip(1).Take(AdxPeriod).Sum();
        double smoothedDmPlus  = dmPlus.Skip(1).Take(AdxPeriod).Sum();
        double smoothedDmMinus = dmMinus.Skip(1).Take(AdxPeriod).Sum();

        var dxList = new List<double>();

        for (int i = AdxPeriod + 1; i < n; i++)
        {
            smoothedTr     = smoothedTr     - smoothedTr     / AdxPeriod + trList[i];
            smoothedDmPlus  = smoothedDmPlus  - smoothedDmPlus  / AdxPeriod + dmPlus[i];
            smoothedDmMinus = smoothedDmMinus - smoothedDmMinus / AdxPeriod + dmMinus[i];

            double diPlus  = smoothedTr > 0 ? 100.0 * smoothedDmPlus  / smoothedTr : 0.0;
            double diMinus = smoothedTr > 0 ? 100.0 * smoothedDmMinus / smoothedTr : 0.0;
            double diSum   = diPlus + diMinus;
            double dx      = diSum > 0 ? 100.0 * Math.Abs(diPlus - diMinus) / diSum : 0.0;

            dxList.Add(dx);
        }

        if (dxList.Count == 0) return 0.0;

        // ADX = simple average of last AdxPeriod DX values (Wilder would smooth, this is an approximation)
        return dxList.TakeLast(AdxPeriod).Average();
    }

    // ── ATR (Average True Range) ───────────────────────────────────────────────

    private static double CalculateAtr(IReadOnlyList<Candle> candles, int period)
    {
        int n = candles.Count;
        if (n < period + 1) return 0.0;

        double atr = 0.0;
        for (int i = n - period; i < n; i++)
        {
            double high  = (double)candles[i].High;
            double low   = (double)candles[i].Low;
            double close = (double)candles[i - 1].Close;

            double tr = Math.Max(high - low, Math.Max(Math.Abs(high - close), Math.Abs(low - close)));
            atr += tr;
        }

        return atr / period;
    }

    // ── ATR rolling average over lookback bars (each bar's ATR averaged) ───────

    private static double CalculateAtrAverage(IReadOnlyList<Candle> candles, int atrPeriod, int lookback)
    {
        int n = candles.Count;
        int start = n - lookback - atrPeriod;
        if (start < 1) start = 1;

        var atrValues = new List<double>();
        for (int end = start + atrPeriod; end <= n; end++)
        {
            var slice = candles.Skip(end - atrPeriod - 1).Take(atrPeriod + 1).ToList();
            atrValues.Add(CalculateAtr(slice, atrPeriod));
        }

        return atrValues.Count > 0 ? atrValues.Average() : CalculateAtr(candles, atrPeriod);
    }

    // ── Crisis helper ─────────────────────────────────────────────────────────

    private static bool HasTrailingSellOff(IReadOnlyList<Candle> candles, int minBearish)
    {
        int count = 0;
        for (int i = candles.Count - 1; i >= 0; i--)
        {
            if (candles[i].Close < candles[i].Open)
                count++;
            else
                break;
        }
        return count >= minBearish;
    }

    // ── BBW rolling average ──────────────────────────────────────────────────

    private static double CalculateBbwRollingAverage(IReadOnlyList<Candle> candles, int bbPeriod, int lookback)
    {
        int n = candles.Count;
        if (n < bbPeriod + lookback) return CalculateBollingerBandWidth(candles, bbPeriod);

        double sum = 0.0;
        int count = 0;
        for (int end = n - lookback; end <= n; end++)
        {
            if (end < bbPeriod) continue;
            var slice = candles.Skip(end - bbPeriod).Take(bbPeriod).ToList();
            sum += CalculateBollingerBandWidth(slice, bbPeriod);
            count++;
        }

        return count > 0 ? sum / count : CalculateBollingerBandWidth(candles, bbPeriod);
    }

    // ── Bollinger Band Width ───────────────────────────────────────────────────

    private static double CalculateBollingerBandWidth(IReadOnlyList<Candle> candles, int period)
    {
        int n = candles.Count;
        if (n < period) return 0.0;

        var closes = candles.TakeLast(period).Select(c => (double)c.Close).ToList();

        double sma    = closes.Average();
        double sumSq  = closes.Sum(c => Math.Pow(c - sma, 2));
        double stdDev = Math.Sqrt(sumSq / period);

        double upper = sma + 2.0 * stdDev;
        double lower = sma - 2.0 * stdDev;

        return sma > 0 ? (upper - lower) / sma : 0.0;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ENGINE CONFIG HELPER
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/>. Returns
    /// <paramref name="defaultValue"/> if the key is absent or the stored value
    /// cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        DbContext ctx,
        string key,
        T defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
