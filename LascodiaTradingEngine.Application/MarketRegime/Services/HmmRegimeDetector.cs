using System.Collections.Concurrent;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.MarketRegime.Services;

/// <summary>
/// Hidden Markov Model (HMM) regime detector with 6 hidden states matching
/// <see cref="MarketRegimeEnum"/>. Uses Gaussian emission probabilities,
/// Baum-Welch training (EM), Viterbi decoding, and the forward algorithm
/// for posterior state probabilities.
///
/// Designed as a lightweight, self-contained component invoked by the hybrid
/// <see cref="MarketRegimeDetector"/>. Trained parameters are cached per
/// symbol+timeframe key for 24 hours to avoid re-running Baum-Welch on every call.
/// </summary>
public sealed class HmmRegimeDetector
{
    // ── Cached HMM parameters ─────────────────────────────────────────────────

    /// <summary>
    /// Holds trained HMM parameters so that subsequent <see cref="Detect"/> calls for the
    /// same symbol+timeframe can skip Baum-Welch and go straight to Viterbi decoding.
    /// </summary>
    private sealed record CachedHmmParams(
        double[]   Pi,
        double[][] A,
        double[][] Means,
        double[][] Variances,
        DateTime   TrainedAt);

    /// <summary>
    /// Per symbol+timeframe cache of trained HMM parameters. Key format: "{symbol}:{timeframe}".
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedHmmParams> _paramCache = new();

    /// <summary>Maximum age of cached parameters before a full re-training is triggered.</summary>
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromHours(24);

    // ── Constants ──────────────────────────────────────────────────────────────

    /// <summary>Number of hidden states (one per <see cref="MarketRegimeEnum"/> value).</summary>
    private const int NumStates = 6;

    /// <summary>Number of observable features extracted from each candle.</summary>
    private const int NumFeatures = 5;

    /// <summary>Maximum Baum-Welch iterations.</summary>
    private const int MaxIterations = 30;

    /// <summary>Convergence threshold for log-likelihood improvement.</summary>
    private const double ConvergenceThreshold = 1e-4;

    /// <summary>Floor value for variances to prevent division-by-zero.</summary>
    private const double VarianceFloor = 1e-6;

    /// <summary>Self-transition probability on the diagonal of the initial transition matrix.</summary>
    private const double SelfTransitionProbability = 0.85;

    // ── Feature indices ───────────────────────────────────────────────────────

    private const int FeatNormalizedAtrChange = 0;
    private const int FeatAdx                 = 1;
    private const int FeatBbw                 = 2;
    private const int FeatVolumeChange        = 3;
    private const int FeatReturnAutocorr      = 4;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Trains an HMM on feature observations extracted from <paramref name="candles"/>,
    /// then returns the most likely current regime and its posterior probability.
    /// Uses cached parameters when available (within 24 hours for the same symbol+timeframe).
    /// </summary>
    /// <param name="candles">Candle history (oldest first). Minimum 30 candles recommended.</param>
    /// <param name="symbol">Trading symbol for cache keying (e.g. "EURUSD"). Optional — when null, no caching.</param>
    /// <param name="timeframe">Timeframe for cache keying. Optional — when null, no caching.</param>
    /// <returns>Tuple of (predicted regime, posterior confidence 0-1).</returns>
    public (MarketRegimeEnum Regime, double Confidence) Detect(
        IReadOnlyList<Candle> candles,
        string? symbol = null,
        Timeframe? timeframe = null)
    {
        if (candles.Count < 20)
            return (MarketRegimeEnum.Ranging, 0.0);

        double[][] observations = ExtractFeatures(candles);
        int T = observations.Length;
        if (T < 5)
            return (MarketRegimeEnum.Ranging, 0.0);

        // ── Resolve HMM parameters (cached or freshly trained) ────────────
        string? cacheKey = symbol is not null && timeframe.HasValue
            ? $"{symbol}:{timeframe.Value}"
            : null;

        double[] pi;
        double[][] A;
        double[][] means;
        double[][] variances;

        bool usedCache = false;

        if (cacheKey is not null &&
            _paramCache.TryGetValue(cacheKey, out var cached) &&
            DateTime.UtcNow - cached.TrainedAt < CacheExpiry)
        {
            // Use cached parameters — skip Baum-Welch.
            pi        = cached.Pi;
            A         = cached.A;
            means     = cached.Means;
            variances = cached.Variances;
            usedCache = true;
        }
        else
        {
            // Full Baum-Welch training.
            pi        = InitializePi();
            A         = InitializeTransitionMatrix();
            means     = InitializeMeans(observations);
            variances = InitializeVariances(observations, means);

            double prevLogLikelihood = double.NegativeInfinity;

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                // E-step: forward-backward
                var (alpha, scalingFactors) = Forward(observations, pi, A, means, variances);
                double[][] beta = Backward(observations, A, means, variances, scalingFactors);

                // Compute log-likelihood from scaling factors
                double logLikelihood = 0.0;
                for (int t = 0; t < T; t++)
                    logLikelihood += Math.Log(Math.Max(scalingFactors[t], 1e-200));

                if (Math.Abs(logLikelihood - prevLogLikelihood) < ConvergenceThreshold)
                    break;
                prevLogLikelihood = logLikelihood;

                // Compute gamma and xi
                double[][] gamma = ComputeGamma(alpha, beta, T);
                double[][][] xi = ComputeXi(alpha, beta, A, means, variances, observations, scalingFactors);

                // M-step: re-estimate parameters
                ReestimatePi(pi, gamma);
                ReestimateTransition(A, gamma, xi, T);
                ReestimateEmissions(means, variances, gamma, observations, T);
            }

            // Cache the trained parameters for future calls.
            if (cacheKey is not null)
            {
                _paramCache[cacheKey] = new CachedHmmParams(pi, A, means, variances, DateTime.UtcNow);
            }
        }

        // ── Viterbi decoding for state sequence ───────────────────────────
        int[] stateSequence = Viterbi(observations, pi, A, means, variances);

        // ── Forward algorithm for posterior of final state ─────────────────
        var (alphaFinal, _) = Forward(observations, pi, A, means, variances);
        double[] posterior = ComputePosterior(alphaFinal, T);

        int predictedState = stateSequence[^1];
        double confidence = posterior[predictedState];

        return ((MarketRegimeEnum)predictedState, confidence);
    }

    /// <summary>
    /// Invalidates cached HMM parameters for the given symbol and timeframe,
    /// forcing the next <see cref="Detect"/> call to run full Baum-Welch training.
    /// </summary>
    /// <param name="symbol">The trading symbol (e.g. "EURUSD").</param>
    /// <param name="timeframe">The candle timeframe.</param>
    public void InvalidateCache(string symbol, Timeframe timeframe)
    {
        string key = $"{symbol}:{timeframe}";
        _paramCache.TryRemove(key, out _);
    }

    // ── Feature extraction ────────────────────────────────────────────────────

    /// <summary>
    /// Extracts a T x NumFeatures observation matrix from candle history.
    /// Each row is one time step; features are:
    ///   [0] Normalized ATR change  (ATR_t - ATR_{t-1}) / ATR_{t-1}
    ///   [1] ADX proxy (directional strength 0-1)
    ///   [2] Bollinger Band Width (normalized)
    ///   [3] Volume change ratio
    ///   [4] Return autocorrelation (lag-1 over rolling window)
    /// </summary>
    private static double[][] ExtractFeatures(IReadOnlyList<Candle> candles)
    {
        int n = candles.Count;
        int atrPeriod = Math.Min(14, n / 3);
        if (atrPeriod < 2) atrPeriod = 2;

        // Pre-compute per-candle true ranges
        var trueRanges = new double[n];
        for (int i = 0; i < n; i++)
        {
            double hl = (double)(candles[i].High - candles[i].Low);
            if (i == 0)
            {
                trueRanges[i] = hl;
                continue;
            }
            double prevClose = (double)candles[i - 1].Close;
            double hc = Math.Abs((double)candles[i].High - prevClose);
            double lc = Math.Abs((double)candles[i].Low - prevClose);
            trueRanges[i] = Math.Max(hl, Math.Max(hc, lc));
        }

        // Pre-compute rolling ATR
        var rollingAtr = new double[n];
        double atrSum = 0;
        for (int i = 0; i < n; i++)
        {
            atrSum += trueRanges[i];
            if (i >= atrPeriod)
                atrSum -= trueRanges[i - atrPeriod];
            int count = Math.Min(i + 1, atrPeriod);
            rollingAtr[i] = atrSum / count;
        }

        // Pre-compute returns
        var returns = new double[n];
        for (int i = 1; i < n; i++)
        {
            double prevClose = (double)candles[i - 1].Close;
            if (prevClose > 0)
                returns[i] = ((double)candles[i].Close - prevClose) / prevClose;
        }

        // Start feature extraction from atrPeriod onward to have stable ATR
        int startIdx = Math.Max(atrPeriod, 2);
        int bbPeriod = Math.Min(20, n - startIdx);
        if (bbPeriod < 3) bbPeriod = 3;

        var featureList = new List<double[]>();

        for (int i = startIdx; i < n; i++)
        {
            var features = new double[NumFeatures];

            // [0] Normalized ATR change
            double prevAtr = rollingAtr[i - 1];
            features[FeatNormalizedAtrChange] = prevAtr > 0
                ? (rollingAtr[i] - prevAtr) / prevAtr
                : 0.0;

            // [1] ADX proxy: ratio of directional movement to ATR (scaled 0-1)
            features[FeatAdx] = ComputeAdxProxy(candles, i, atrPeriod, rollingAtr[i]);

            // [2] Bollinger Band Width
            int bbStart = Math.Max(0, i - bbPeriod + 1);
            features[FeatBbw] = ComputeBbw(candles, bbStart, i + 1);

            // [3] Volume change
            if (i > 0 && candles[i - 1].Volume > 0)
                features[FeatVolumeChange] = (double)(candles[i].Volume - candles[i - 1].Volume)
                                             / (double)candles[i - 1].Volume;

            // [4] Return autocorrelation (lag-1 over last 10 bars)
            int acWindow = Math.Min(10, i);
            features[FeatReturnAutocorr] = ComputeReturnAutocorrelation(returns, i, acWindow);

            featureList.Add(features);
        }

        return featureList.ToArray();
    }

    private static double ComputeAdxProxy(IReadOnlyList<Candle> candles, int endIdx, int period, double atr)
    {
        if (atr <= 0 || endIdx < 1) return 0.0;

        double dmPlusSum = 0, dmMinusSum = 0;
        int start = Math.Max(1, endIdx - period + 1);
        int count = 0;

        for (int i = start; i <= endIdx; i++)
        {
            double upMove = (double)(candles[i].High - candles[i - 1].High);
            double downMove = (double)(candles[i - 1].Low - candles[i].Low);

            if (upMove > downMove && upMove > 0) dmPlusSum += upMove;
            if (downMove > upMove && downMove > 0) dmMinusSum += downMove;
            count++;
        }

        if (count == 0) return 0.0;
        double diPlus = dmPlusSum / count / atr;
        double diMinus = dmMinusSum / count / atr;
        double diSum = diPlus + diMinus;

        // DX normalized to 0-1
        return diSum > 0 ? Math.Abs(diPlus - diMinus) / diSum : 0.0;
    }

    private static double ComputeBbw(IReadOnlyList<Candle> candles, int start, int end)
    {
        int count = end - start;
        if (count < 2) return 0.0;

        double sum = 0;
        for (int i = start; i < end; i++)
            sum += (double)candles[i].Close;
        double sma = sum / count;
        if (sma <= 0) return 0.0;

        double sumSq = 0;
        for (int i = start; i < end; i++)
        {
            double diff = (double)candles[i].Close - sma;
            sumSq += diff * diff;
        }
        double stdDev = Math.Sqrt(sumSq / count);
        double upper = sma + 2.0 * stdDev;
        double lower = sma - 2.0 * stdDev;
        return (upper - lower) / sma; // standard BBW formula aligned with MarketRegimeDetector
    }

    private static double ComputeReturnAutocorrelation(double[] returns, int endIdx, int window)
    {
        if (window < 3) return 0.0;

        int start = endIdx - window + 1;
        if (start < 1) start = 1;
        int actualWindow = endIdx - start + 1;
        if (actualWindow < 3) return 0.0;

        // Mean of returns in window
        double mean = 0;
        for (int i = start; i <= endIdx; i++)
            mean += returns[i];
        mean /= actualWindow;

        // Lag-1 autocorrelation
        double numerator = 0, denominator = 0;
        for (int i = start + 1; i <= endIdx; i++)
        {
            double ri = returns[i] - mean;
            double riLag = returns[i - 1] - mean;
            numerator += ri * riLag;
            denominator += ri * ri;
        }

        return denominator > 0 ? numerator / denominator : 0.0;
    }

    // ── HMM initialization ────────────────────────────────────────────────────

    private static double[] InitializePi()
    {
        // Uniform initial state distribution
        var pi = new double[NumStates];
        for (int i = 0; i < NumStates; i++)
            pi[i] = 1.0 / NumStates;
        return pi;
    }

    private static double[][] InitializeTransitionMatrix()
    {
        var A = new double[NumStates][];
        double offDiag = (1.0 - SelfTransitionProbability) / (NumStates - 1);

        for (int i = 0; i < NumStates; i++)
        {
            A[i] = new double[NumStates];
            for (int j = 0; j < NumStates; j++)
                A[i][j] = (i == j) ? SelfTransitionProbability : offDiag;
        }

        return A;
    }

    /// <summary>
    /// Initializes emission means using k-means++ clustering for better Baum-Welch
    /// convergence. K-means++ selects initial centroids with probability proportional
    /// to squared distance from nearest existing centroid, then refines via Lloyd's
    /// algorithm (20 iterations, convergence threshold 1e-6).
    /// Falls back to sequential segmentation if k-means fails to converge or
    /// observations are too few.
    /// </summary>
    private static double[][] InitializeMeans(double[][] observations)
    {
        int T = observations.Length;

        // Fallback to sequential segmentation for very small datasets
        if (T < NumStates * 3)
            return InitializeMeansSequential(observations);

        // ── K-means++ seeding ────────��────────────────────────────────────
        var rng = new Random(42); // deterministic seed for reproducibility
        var centroids = new double[NumStates][];

        // First centroid: random observation
        centroids[0] = (double[])observations[rng.Next(T)].Clone();

        // Subsequent centroids: probability proportional to D(x)²
        var minDistSq = new double[T];
        for (int k = 1; k < NumStates; k++)
        {
            double totalDistSq = 0.0;
            for (int t = 0; t < T; t++)
            {
                double nearest = double.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    double d = SquaredDistance(observations[t], centroids[c]);
                    if (d < nearest) nearest = d;
                }
                minDistSq[t] = nearest;
                totalDistSq += nearest;
            }

            if (totalDistSq < 1e-15)
            {
                // All observations are identical — fall back to sequential
                return InitializeMeansSequential(observations);
            }

            // Weighted random selection
            double threshold = rng.NextDouble() * totalDistSq;
            double cumulative = 0.0;
            int selected = T - 1; // fallback to last
            for (int t = 0; t < T; t++)
            {
                cumulative += minDistSq[t];
                if (cumulative >= threshold) { selected = t; break; }
            }
            centroids[k] = (double[])observations[selected].Clone();
        }

        // ── Lloyd's algorithm (k-means refinement) ────────────────────────
        var assignments = new int[T];
        const int MaxIterations = 20;
        const double ConvergenceThreshold = 1e-6;

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            // Assignment step: assign each observation to nearest centroid
            for (int t = 0; t < T; t++)
            {
                double bestDist = double.MaxValue;
                int bestCluster = 0;
                for (int k = 0; k < NumStates; k++)
                {
                    double d = SquaredDistance(observations[t], centroids[k]);
                    if (d < bestDist) { bestDist = d; bestCluster = k; }
                }
                assignments[t] = bestCluster;
            }

            // Update step: recompute centroids
            var newCentroids = new double[NumStates][];
            var counts = new int[NumStates];
            for (int k = 0; k < NumStates; k++)
                newCentroids[k] = new double[NumFeatures];

            for (int t = 0; t < T; t++)
            {
                int k = assignments[t];
                counts[k]++;
                for (int f = 0; f < NumFeatures; f++)
                    newCentroids[k][f] += observations[t][f];
            }

            double maxShift = 0.0;
            for (int k = 0; k < NumStates; k++)
            {
                if (counts[k] == 0)
                {
                    // Empty cluster — reinitialize from farthest point
                    double maxDist = 0;
                    int farthest = 0;
                    for (int t = 0; t < T; t++)
                    {
                        double d = SquaredDistance(observations[t], centroids[k]);
                        if (d > maxDist) { maxDist = d; farthest = t; }
                    }
                    newCentroids[k] = (double[])observations[farthest].Clone();
                    counts[k] = 1;
                }
                else
                {
                    for (int f = 0; f < NumFeatures; f++)
                        newCentroids[k][f] /= counts[k];
                }

                double shift = SquaredDistance(centroids[k], newCentroids[k]);
                if (shift > maxShift) maxShift = shift;
            }

            centroids = newCentroids;

            if (maxShift < ConvergenceThreshold)
                break; // converged
        }

        return centroids;
    }

    /// <summary>
    /// Fallback: sequential segmentation for very small datasets (T &lt; NumStates × 3).
    /// Divides observations into NumStates equal segments and computes per-segment means.
    /// </summary>
    private static double[][] InitializeMeansSequential(double[][] observations)
    {
        int T = observations.Length;
        var means = new double[NumStates][];
        int segLen = Math.Max(1, T / NumStates);

        for (int s = 0; s < NumStates; s++)
        {
            means[s] = new double[NumFeatures];
            int start = s * segLen;
            int end = (s == NumStates - 1) ? T : Math.Min((s + 1) * segLen, T);
            int count = end - start;
            if (count <= 0) { start = 0; end = T; count = T; }

            for (int t = start; t < end; t++)
                for (int f = 0; f < NumFeatures; f++)
                    means[s][f] += observations[t][f];

            for (int f = 0; f < NumFeatures; f++)
                means[s][f] /= count;
        }

        return means;
    }

    /// <summary>Squared Euclidean distance between two feature vectors.</summary>
    private static double SquaredDistance(double[] a, double[] b)
    {
        double sum = 0.0;
        int len = Math.Min(a.Length, b.Length);
        for (int f = 0; f < len; f++)
        {
            double d = a[f] - b[f];
            sum += d * d;
        }
        return sum;
    }

    /// <summary>
    /// Initializes emission variances from k-means cluster assignments.
    /// Each state's variance is computed from observations assigned to that cluster.
    /// Falls back to global variance for empty clusters.
    /// </summary>
    private static double[][] InitializeVariances(double[][] observations, double[][] means)
    {
        int T = observations.Length;
        var variances = new double[NumStates][];
        var counts = new int[NumStates];

        // Assign each observation to nearest mean (same as k-means final assignment)
        for (int s = 0; s < NumStates; s++)
            variances[s] = new double[NumFeatures];

        for (int t = 0; t < T; t++)
        {
            double bestDist = double.MaxValue;
            int bestState = 0;
            for (int s = 0; s < NumStates; s++)
            {
                double d = SquaredDistance(observations[t], means[s]);
                if (d < bestDist) { bestDist = d; bestState = s; }
            }

            counts[bestState]++;
            for (int f = 0; f < NumFeatures; f++)
            {
                double diff = observations[t][f] - means[bestState][f];
                variances[bestState][f] += diff * diff;
            }
        }

        // Compute global variance as fallback for empty clusters
        var globalVariance = new double[NumFeatures];
        var globalMean = new double[NumFeatures];
        for (int t = 0; t < T; t++)
            for (int f = 0; f < NumFeatures; f++)
                globalMean[f] += observations[t][f];
        for (int f = 0; f < NumFeatures; f++)
            globalMean[f] /= T;
        for (int t = 0; t < T; t++)
            for (int f = 0; f < NumFeatures; f++)
            {
                double diff = observations[t][f] - globalMean[f];
                globalVariance[f] += diff * diff;
            }
        for (int f = 0; f < NumFeatures; f++)
            globalVariance[f] = Math.Max(globalVariance[f] / T, VarianceFloor);

        for (int s = 0; s < NumStates; s++)
        {
            if (counts[s] < 2)
            {
                // Empty or singleton cluster — use global variance
                variances[s] = (double[])globalVariance.Clone();
            }
            else
            {
                for (int f = 0; f < NumFeatures; f++)
                    variances[s][f] = Math.Max(variances[s][f] / counts[s], VarianceFloor);
            }
        }

        return variances;
    }

    // ── Gaussian emission probability ─────────────────────────────────────────

    /// <summary>
    /// Computes the emission probability of observation <paramref name="obs"/> given
    /// state <paramref name="state"/> under diagonal-covariance Gaussian.
    /// Uses log-space internally then exponentiates for numerical stability.
    /// </summary>
    private static double EmissionProbability(double[] obs, int state, double[][] means, double[][] variances)
    {
        double logProb = 0.0;
        for (int f = 0; f < NumFeatures; f++)
        {
            double diff = obs[f] - means[state][f];
            double var_ = Math.Max(variances[state][f], VarianceFloor);
            logProb += -0.5 * Math.Log(2.0 * Math.PI * var_) - 0.5 * diff * diff / var_;
        }

        logProb = Math.Max(logProb, -500.0); // prevent -Infinity from zero-variance features

        return Math.Exp(logProb);
    }

    // ── Forward algorithm (scaled) ────────────────────────────────────────────

    private static (double[][] Alpha, double[] ScalingFactors) Forward(
        double[][] observations,
        double[] pi,
        double[][] A,
        double[][] means,
        double[][] variances)
    {
        int T = observations.Length;
        var alpha = new double[T][];
        var c = new double[T]; // scaling factors

        // t = 0
        alpha[0] = new double[NumStates];
        for (int s = 0; s < NumStates; s++)
            alpha[0][s] = pi[s] * EmissionProbability(observations[0], s, means, variances);

        c[0] = 0;
        for (int s = 0; s < NumStates; s++)
            c[0] += alpha[0][s];
        c[0] = Math.Max(c[0], double.Epsilon);
        for (int s = 0; s < NumStates; s++)
            alpha[0][s] /= c[0];

        // t = 1..T-1
        for (int t = 1; t < T; t++)
        {
            alpha[t] = new double[NumStates];
            for (int j = 0; j < NumStates; j++)
            {
                double sum = 0;
                for (int i = 0; i < NumStates; i++)
                    sum += alpha[t - 1][i] * A[i][j];
                alpha[t][j] = sum * EmissionProbability(observations[t], j, means, variances);
            }

            c[t] = 0;
            for (int s = 0; s < NumStates; s++)
                c[t] += alpha[t][s];
            c[t] = Math.Max(c[t], double.Epsilon);
            for (int s = 0; s < NumStates; s++)
                alpha[t][s] /= c[t];
        }

        return (alpha, c);
    }

    // ── Backward algorithm (scaled) ───────────────────────────────────────────

    private static double[][] Backward(
        double[][] observations,
        double[][] A,
        double[][] means,
        double[][] variances,
        double[] scalingFactors)
    {
        int T = observations.Length;
        var beta = new double[T][];

        // t = T-1
        beta[T - 1] = new double[NumStates];
        for (int s = 0; s < NumStates; s++)
            beta[T - 1][s] = 1.0 / Math.Max(scalingFactors[T - 1], double.Epsilon);

        // t = T-2..0
        for (int t = T - 2; t >= 0; t--)
        {
            beta[t] = new double[NumStates];
            for (int i = 0; i < NumStates; i++)
            {
                double sum = 0;
                for (int j = 0; j < NumStates; j++)
                    sum += A[i][j] * EmissionProbability(observations[t + 1], j, means, variances) * beta[t + 1][j];
                beta[t][i] = sum / Math.Max(scalingFactors[t], double.Epsilon);
            }
        }

        return beta;
    }

    // ── Gamma (posterior state probability) ────────────────────────────────────

    private static double[][] ComputeGamma(double[][] alpha, double[][] beta, int T)
    {
        var gamma = new double[T][];
        for (int t = 0; t < T; t++)
        {
            gamma[t] = new double[NumStates];
            double sum = 0;
            for (int s = 0; s < NumStates; s++)
            {
                gamma[t][s] = alpha[t][s] * beta[t][s];
                sum += gamma[t][s];
            }
            if (sum > 0)
                for (int s = 0; s < NumStates; s++)
                    gamma[t][s] /= sum;
        }
        return gamma;
    }

    // ── Xi (joint state transition probability) ───────────────────────────────

    private static double[][][] ComputeXi(
        double[][] alpha,
        double[][] beta,
        double[][] A,
        double[][] means,
        double[][] variances,
        double[][] observations,
        double[] scalingFactors)
    {
        int T = observations.Length;
        var xi = new double[T - 1][][];

        for (int t = 0; t < T - 1; t++)
        {
            xi[t] = new double[NumStates][];
            double denom = 0;
            for (int i = 0; i < NumStates; i++)
            {
                xi[t][i] = new double[NumStates];
                for (int j = 0; j < NumStates; j++)
                {
                    xi[t][i][j] = alpha[t][i] * A[i][j]
                                  * EmissionProbability(observations[t + 1], j, means, variances)
                                  * beta[t + 1][j];
                    denom += xi[t][i][j];
                }
            }

            if (denom > 0)
                for (int i = 0; i < NumStates; i++)
                    for (int j = 0; j < NumStates; j++)
                        xi[t][i][j] /= denom;
        }

        return xi;
    }

    // ── M-step: re-estimation ─────────────────────────────────────────────────

    private static void ReestimatePi(double[] pi, double[][] gamma)
    {
        double sum = 0;
        for (int s = 0; s < NumStates; s++)
        {
            pi[s] = gamma[0][s];
            sum += pi[s];
        }
        if (sum > 0)
            for (int s = 0; s < NumStates; s++)
                pi[s] /= sum;
    }

    private static void ReestimateTransition(double[][] A, double[][] gamma, double[][][] xi, int T)
    {
        for (int i = 0; i < NumStates; i++)
        {
            double gammaSum = 0;
            for (int t = 0; t < T - 1; t++)
                gammaSum += gamma[t][i];

            for (int j = 0; j < NumStates; j++)
            {
                double xiSum = 0;
                for (int t = 0; t < T - 1; t++)
                    xiSum += xi[t][i][j];

                A[i][j] = gammaSum > 0 ? xiSum / gammaSum : 1.0 / NumStates;
            }

            // Normalize row
            double rowSum = 0;
            for (int j = 0; j < NumStates; j++)
                rowSum += A[i][j];
            if (rowSum > 0)
                for (int j = 0; j < NumStates; j++)
                    A[i][j] /= rowSum;
        }
    }

    private static void ReestimateEmissions(
        double[][] means,
        double[][] variances,
        double[][] gamma,
        double[][] observations,
        int T)
    {
        for (int s = 0; s < NumStates; s++)
        {
            double gammaSum = 0;
            for (int t = 0; t < T; t++)
                gammaSum += gamma[t][s];

            if (gammaSum < double.Epsilon)
                continue;

            // Re-estimate means
            for (int f = 0; f < NumFeatures; f++)
            {
                double weightedSum = 0;
                for (int t = 0; t < T; t++)
                    weightedSum += gamma[t][s] * observations[t][f];
                means[s][f] = weightedSum / gammaSum;
            }

            // Re-estimate variances
            for (int f = 0; f < NumFeatures; f++)
            {
                double weightedSumSq = 0;
                for (int t = 0; t < T; t++)
                {
                    double diff = observations[t][f] - means[s][f];
                    weightedSumSq += gamma[t][s] * diff * diff;
                }
                variances[s][f] = Math.Max(weightedSumSq / gammaSum, VarianceFloor);
            }
        }
    }

    // ── Viterbi decoding ──────────────────────────────────────────────────────

    /// <summary>
    /// Finds the most likely state sequence using the Viterbi algorithm.
    /// Works in log-space to avoid numerical underflow.
    /// </summary>
    private static int[] Viterbi(
        double[][] observations,
        double[] pi,
        double[][] A,
        double[][] means,
        double[][] variances)
    {
        int T = observations.Length;

        if (T < 2)
        {
            // Too few observations for Viterbi — return prior-based best state
            int bestState = Enumerable.Range(0, NumStates).MaxBy(s => Math.Log(Math.Max(pi[s], double.Epsilon)));
            return new[] { bestState };
        }

        var delta = new double[T][];
        var psi = new int[T][];

        // t = 0
        delta[0] = new double[NumStates];
        psi[0] = new int[NumStates];
        for (int s = 0; s < NumStates; s++)
        {
            double emProb = EmissionProbability(observations[0], s, means, variances);
            delta[0][s] = Math.Log(Math.Max(pi[s], double.Epsilon))
                          + Math.Log(Math.Max(emProb, double.Epsilon));
            psi[0][s] = 0;
        }

        // t = 1..T-1
        for (int t = 1; t < T; t++)
        {
            delta[t] = new double[NumStates];
            psi[t] = new int[NumStates];

            for (int j = 0; j < NumStates; j++)
            {
                double maxVal = double.NegativeInfinity;
                int maxIdx = 0;

                for (int i = 0; i < NumStates; i++)
                {
                    double val = delta[t - 1][i] + Math.Log(Math.Max(A[i][j], double.Epsilon));
                    if (val > maxVal)
                    {
                        maxVal = val;
                        maxIdx = i;
                    }
                }

                double emProb = EmissionProbability(observations[t], j, means, variances);
                delta[t][j] = maxVal + Math.Log(Math.Max(emProb, double.Epsilon));
                psi[t][j] = maxIdx;
            }
        }

        // Backtrack
        var path = new int[T];
        double bestFinal = double.NegativeInfinity;
        for (int s = 0; s < NumStates; s++)
        {
            if (delta[T - 1][s] > bestFinal)
            {
                bestFinal = delta[T - 1][s];
                path[T - 1] = s;
            }
        }

        for (int t = T - 2; t >= 0; t--)
            path[t] = psi[t + 1][path[t + 1]];

        return path;
    }

    // ── Posterior from forward pass ────────────────────────────────────────────

    /// <summary>
    /// Computes the posterior state distribution at the last time step
    /// from the (scaled) forward variables.
    /// </summary>
    private static double[] ComputePosterior(double[][] alpha, int T)
    {
        var posterior = new double[NumStates];
        double sum = 0;

        for (int s = 0; s < NumStates; s++)
        {
            posterior[s] = alpha[T - 1][s];
            sum += posterior[s];
        }

        if (sum > 0)
            for (int s = 0; s < NumStates; s++)
                posterior[s] /= sum;

        return posterior;
    }
}
