namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Pure, allocation-light macro feature calculators shared by the inference-time
/// <see cref="MacroFeatureProvider"/> and the training-time feature builder. No DI,
/// no DB access — callers pre-load cross-pair candle closes and pass them in.
///
/// Each method mirrors the inference-time implementation byte-for-byte so that
/// features seen during training match features seen during scoring.
/// </summary>
public static class MacroFeatureCalculator
{
    /// <summary>90-bar drift of the pair's log-returns normalised by rolling volatility.</summary>
    public static double ComputePairCarryProxy(double[] closes)
    {
        if (closes is null || closes.Length < 91) return double.NaN;

        var logReturns = new double[90];
        int end = closes.Length - 1;
        for (int i = 0; i < 90; i++)
        {
            double prev = closes[end - 90 + i];
            double curr = closes[end - 89 + i];
            if (prev <= 0 || curr <= 0) return double.NaN;
            logReturns[i] = System.Math.Log(curr / prev);
        }

        double mean = 0.0;
        for (int i = 0; i < 90; i++) mean += logReturns[i];
        mean /= 90;

        double variance = 0.0;
        for (int i = 0; i < 90; i++)
        {
            double d = logReturns[i] - mean;
            variance += d * d;
        }
        variance /= 89;
        double stddev = System.Math.Sqrt(variance);

        if (stddev < 1e-12) return 0.0;
        return System.Math.Clamp((mean / stddev) * System.Math.Sqrt(90), -3.0, 3.0);
    }

    /// <summary>Risk-on / risk-off gauge from USDJPY/USDCHF vs AUDUSD/NZDUSD/EURUSD.</summary>
    public static double ComputeSafeHavenIndex(System.Collections.Generic.Dictionary<string, double[]> basket)
    {
        if (!basket.TryGetValue("USDJPY", out var jpy) || jpy.Length < 60) return double.NaN;
        if (!basket.TryGetValue("USDCHF", out var chf) || chf.Length < 60) return double.NaN;
        if (!basket.TryGetValue("AUDUSD", out var aud) || aud.Length < 60) return double.NaN;
        if (!basket.TryGetValue("NZDUSD", out var nzd) || nzd.Length < 60) return double.NaN;
        if (!basket.TryGetValue("EURUSD", out var eur) || eur.Length < 60) return double.NaN;

        int n = System.Math.Min(jpy.Length, System.Math.Min(chf.Length, System.Math.Min(aud.Length, System.Math.Min(nzd.Length, eur.Length))));
        if (n < 60) return double.NaN;
        int start = n - 60;

        double jpy0 = jpy[start], chf0 = chf[start], aud0 = aud[start], nzd0 = nzd[start], eur0 = eur[start];
        if (jpy0 <= 0 || chf0 <= 0 || aud0 <= 0 || nzd0 <= 0 || eur0 <= 0) return double.NaN;

        var series = new double[60];
        for (int i = 0; i < 60; i++)
        {
            double num = (jpy[start + i] / jpy0 + chf[start + i] / chf0) / 2.0;
            double den = (aud[start + i] / aud0 + nzd[start + i] / nzd0 + eur[start + i] / eur0) / 3.0;
            series[i] = den > 0 ? num / den : 1.0;
        }

        double mean = 0.0;
        for (int i = 0; i < 60; i++) mean += series[i];
        mean /= 60;
        double variance = 0.0;
        for (int i = 0; i < 60; i++) { double d = series[i] - mean; variance += d * d; }
        variance /= 59;
        double stddev = System.Math.Sqrt(variance);

        if (stddev < 1e-12) return 0.0;
        return System.Math.Clamp((series[^1] - mean) / stddev, -3.0, 3.0);
    }

    /// <summary>DXY-style z-scored mean 20-bar log-return across G10 USD pairs.</summary>
    public static double ComputeDollarStrengthComposite(System.Collections.Generic.Dictionary<string, double[]> basket)
    {
        var quoteUsd = new[] { "EURUSD", "GBPUSD", "AUDUSD", "NZDUSD" };
        var baseUsd = new[] { "USDJPY", "USDCHF", "USDCAD" };

        var returns = new System.Collections.Generic.List<double>(7);
        foreach (var sym in quoteUsd)
        {
            if (!basket.TryGetValue(sym, out var arr) || arr.Length < 21) continue;
            double prev = arr[^21]; double curr = arr[^1];
            if (prev <= 0 || curr <= 0) continue;
            returns.Add(-System.Math.Log(curr / prev));
        }
        foreach (var sym in baseUsd)
        {
            if (!basket.TryGetValue(sym, out var arr) || arr.Length < 21) continue;
            double prev = arr[^21]; double curr = arr[^1];
            if (prev <= 0 || curr <= 0) continue;
            returns.Add(System.Math.Log(curr / prev));
        }
        if (returns.Count < 4) return double.NaN;

        double mean = System.Linq.Enumerable.Average(returns);
        double variance = System.Linq.Enumerable.Sum(returns, r => (r - mean) * (r - mean)) / System.Math.Max(1, returns.Count - 1);
        double stddev = System.Math.Sqrt(variance);

        if (stddev < 1e-12) return 0.0;
        return System.Math.Clamp((mean / stddev) * System.Math.Sqrt(returns.Count), -3.0, 3.0);
    }

    /// <summary>Std dev of pairwise Pearson correlations across 20-bar log-returns of G10 USD pairs.</summary>
    public static double ComputeCrossPairCorrelationStress(System.Collections.Generic.Dictionary<string, double[]> basket)
    {
        var series = new System.Collections.Generic.List<(string Sym, double[] Returns)>();
        foreach (var kvp in basket)
        {
            var arr = kvp.Value;
            if (arr.Length < 21) continue;
            var returns = new double[20];
            int tail = arr.Length;
            bool bad = false;
            for (int i = 0; i < 20; i++)
            {
                double prev = arr[tail - 21 + i];
                double curr = arr[tail - 20 + i];
                if (prev <= 0 || curr <= 0) { bad = true; break; }
                returns[i] = System.Math.Log(curr / prev);
            }
            if (!bad) series.Add((kvp.Key, returns));
        }
        if (series.Count < 3) return double.NaN;

        var correlations = new System.Collections.Generic.List<double>();
        for (int i = 0; i < series.Count; i++)
        for (int j = i + 1; j < series.Count; j++)
        {
            double corr = PearsonCorrelation(series[i].Returns, series[j].Returns);
            if (!double.IsNaN(corr)) correlations.Add(corr);
        }
        if (correlations.Count < 2) return double.NaN;

        double meanC = System.Linq.Enumerable.Average(correlations);
        double varianceC = System.Linq.Enumerable.Sum(correlations, c => (c - meanC) * (c - meanC)) / (correlations.Count - 1);
        return System.Math.Clamp(System.Math.Sqrt(varianceC), 0.0, 1.0);
    }

    public static double PearsonCorrelation(double[] x, double[] y)
    {
        if (x.Length != y.Length || x.Length < 2) return double.NaN;
        double meanX = System.Linq.Enumerable.Average(x), meanY = System.Linq.Enumerable.Average(y);
        double num = 0.0, denX = 0.0, denY = 0.0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            num += dx * dy;
            denX += dx * dx;
            denY += dy * dy;
        }
        double den = System.Math.Sqrt(denX * denY);
        return den > 1e-12 ? num / den : double.NaN;
    }

    /// <summary>
    /// Given a basket of time-sorted (timestamp, close) pairs per symbol and an as-of cutoff,
    /// returns a dictionary of close-price arrays truncated to include only bars at or before the cutoff.
    /// Used by the training-time feature builder to compute point-in-time macro features per sample
    /// without issuing per-sample DB queries.
    /// </summary>
    public static System.Collections.Generic.Dictionary<string, double[]> SliceBasketAsOf(
        System.Collections.Generic.Dictionary<string, (System.DateTime[] Times, double[] Closes)> fullBasket,
        System.DateTime asOfUtc,
        int maxBars = 120)
    {
        var result = new System.Collections.Generic.Dictionary<string, double[]>(
            fullBasket.Count, System.StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in fullBasket)
        {
            var times = kvp.Value.Times;
            var closes = kvp.Value.Closes;
            if (times.Length == 0) continue;

            // Binary search for last index <= asOfUtc.
            int lo = 0, hi = times.Length - 1, idx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (times[mid] <= asOfUtc) { idx = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (idx < 0) continue;

            int count = System.Math.Min(maxBars, idx + 1);
            int start = idx + 1 - count;
            var slice = new double[count];
            System.Array.Copy(closes, start, slice, 0, count);
            result[kvp.Key] = slice;
        }
        return result;
    }
}
