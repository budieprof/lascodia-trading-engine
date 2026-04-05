using System.Collections.Concurrent;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Tree-structured Parzen Estimator (TPE) for Bayesian hyperparameter optimization.
/// Maintains two kernel density models — one for "good" observations (above the gamma
/// quantile) and one for "bad" — and proposes candidates that maximise the Expected
/// Improvement ratio l(x)/g(x).
///
/// This is a simplified but functional implementation suitable for low-dimensional
/// parameter spaces (2-5 dimensions) typically found in trading strategy optimisation.
/// </summary>
internal sealed class TreeParzenEstimator
{
    private readonly List<(Dictionary<string, double> Params, double Score)> _observations = [];
    private readonly Dictionary<string, (double Min, double Max, bool IsInteger)> _bounds;
    private readonly double _gamma;
    private readonly DeterministicRandom _rng;
    private readonly double _bandwidthScale;

    /// <param name="bounds">Parameter name → (min, max, isInteger) for each dimension.</param>
    /// <param name="gamma">
    /// Quantile threshold separating "good" from "bad". Pass -1 to auto-select based on
    /// dimensionality: gamma = 0.15 + 0.03 * dims (sharper for 2D, wider for 5D to avoid
    /// starving the good model in higher dimensions).
    /// </param>
    /// <param name="seed">Random seed for reproducibility.</param>
    /// <param name="bandwidthScale">Silverman bandwidth multiplier (default 1.0).</param>
    public TreeParzenEstimator(
        Dictionary<string, (double Min, double Max, bool IsInteger)> bounds,
        double gamma = -1,
        int seed = 42,
        double bandwidthScale = 1.0,
        ulong? randomState = null)
    {
        _bounds         = bounds;
        _gamma          = gamma < 0 ? Math.Clamp(0.15 + 0.03 * bounds.Count, 0.10, 0.50) : gamma;
        _rng            = randomState.HasValue ? new DeterministicRandom(randomState.Value) : new DeterministicRandom(seed);
        _bandwidthScale = bandwidthScale;
    }

    public int ObservationCount => _observations.Count;
    public ulong RandomState => _rng.State;

    /// <summary>Records a completed evaluation.</summary>
    public void AddObservation(Dictionary<string, double> parameters, double score)
        => AddObservation(parameters, score, weight: 1.0);

    /// <summary>
    /// Records a completed evaluation with a weight. Weights &lt; 1.0 reduce the
    /// observation's influence on the TPE model — used for non-current-regime
    /// warm-start observations. Weight is applied by blending the score toward
    /// the observation mean, reducing the observation's discriminative power.
    /// </summary>
    public void AddObservation(Dictionary<string, double> parameters, double score, double weight)
    {
        if (weight < 1.0 && weight > 0.0 && _observations.Count > 0)
        {
            double mean = _observations.Average(o => o.Score);
            score = mean + (score - mean) * weight;
        }
        _observations.Add((new Dictionary<string, double>(parameters), score));
    }

    /// <summary>
    /// Proposes the next batch of candidates to evaluate, using TPE-style acquisition.
    /// Falls back to Latin Hypercube Sampling when fewer than <paramref name="minObservationsForModel"/>
    /// observations are available.
    /// </summary>
    public List<Dictionary<string, double>> SuggestCandidates(
        int count,
        int minObservationsForModel = 10,
        int drawsPerCandidate = 200)
    {
        if (_observations.Count < minObservationsForModel)
            return LatinHypercubeSample(count);

        // Split observations into good (top gamma quantile) and bad (rest).
        // If gamma is too aggressive and leaves no bad observations, progressively
        // widen the cutoff until bad has at least 1 member (avoids l(x)/g(x) ≈ 1
        // everywhere which destroys the acquisition signal).
        var sorted = _observations.OrderByDescending(o => o.Score).ToList();
        int nGood  = Math.Max(1, (int)(sorted.Count * _gamma));
        if (nGood >= sorted.Count) nGood = Math.Max(1, sorted.Count - 1);
        var good   = sorted.Take(nGood).Select(o => o.Params).ToList();
        var bad    = sorted.Skip(nGood).Select(o => o.Params).ToList();

        if (bad.Count == 0)
        {
            // Fallback: use bottom half as bad to maintain acquisition signal
            int halfIdx = Math.Max(1, sorted.Count / 2);
            good = sorted.Take(halfIdx).Select(o => o.Params).ToList();
            bad  = sorted.Skip(halfIdx).Select(o => o.Params).ToList();
            if (bad.Count == 0) return LatinHypercubeSample(count); // All identical — explore randomly
        }

        var candidates = new List<(Dictionary<string, double> Params, double EI)>();

        // Sample many points from l(x) (good distribution), score by l(x)/g(x)
        for (int i = 0; i < count * drawsPerCandidate; i++)
        {
            var candidate = SampleFromKde(good);
            ClampToBounds(candidate);

            double lx = KernelDensity(candidate, good);
            double gx = KernelDensity(candidate, bad);

            // EI ≈ l(x) / max(g(x), epsilon) — higher is better
            double ei = lx / Math.Max(gx, 1e-12);
            candidates.Add((candidate, ei));
        }

        // Acquisition diversity: reserve 20% of slots for pure random exploration
        // to prevent surrogate tunnel vision in deceptive landscapes
        int exploitCount = Math.Max(1, (int)(count * 0.8));
        int exploreCount = count - exploitCount;

        var result = candidates
            .OrderByDescending(c => c.EI)
            .Take(exploitCount)
            .Select(c => c.Params)
            .ToList();

        if (exploreCount > 0)
            result.AddRange(LatinHypercubeSample(exploreCount));

        return result;
    }

    /// <summary>
    /// Retrieves the historical approved parameter ranges for a given dimension,
    /// used by adaptive search bounds. Returns (narrowedMin, narrowedMax) if enough
    /// good observations exist, otherwise returns the original bounds.
    /// </summary>
    public (double Min, double Max) GetAdaptiveBounds(string paramName, double shrinkFactor = 0.8)
    {
        if (!_bounds.TryGetValue(paramName, out var originalBounds))
            return (0, 1);

        var sorted = _observations.OrderByDescending(o => o.Score).ToList();
        int nGood  = Math.Max(1, (int)(sorted.Count * _gamma));
        var goodValues = sorted.Take(nGood)
            .Where(o => o.Params.ContainsKey(paramName))
            .Select(o => o.Params[paramName])
            .ToList();

        if (goodValues.Count < 3) return (originalBounds.Min, originalBounds.Max);

        double goodMin = goodValues.Min();
        double goodMax = goodValues.Max();
        double range   = originalBounds.Max - originalBounds.Min;
        double margin  = range * (1.0 - shrinkFactor) / 2.0;

        double adaptedMin = Math.Max(originalBounds.Min, goodMin - margin);
        double adaptedMax = Math.Min(originalBounds.Max, goodMax + margin);

        // Ensure at least 20% of the original range to maintain exploration
        if (adaptedMax - adaptedMin < range * 0.2)
        {
            double center = (goodMin + goodMax) / 2.0;
            adaptedMin = Math.Max(originalBounds.Min, center - range * 0.1);
            adaptedMax = Math.Min(originalBounds.Max, center + range * 0.1);
        }

        return (adaptedMin, adaptedMax);
    }

    // ── Latin Hypercube Sampling (initial exploration) ──────────────────────

    private List<Dictionary<string, double>> LatinHypercubeSample(int count)
    {
        var results = new List<Dictionary<string, double>>();
        var keys    = _bounds.Keys.ToList();

        // Create stratified bins per dimension
        var permutations = new Dictionary<string, int[]>();
        foreach (var key in keys)
        {
            var perm = Enumerable.Range(0, count).ToArray();
            Shuffle(perm);
            permutations[key] = perm;
        }

        for (int i = 0; i < count; i++)
        {
            var candidate = new Dictionary<string, double>();
            foreach (var key in keys)
            {
                var (min, max, isInt) = _bounds[key];
                int bin      = permutations[key][i];
                double lower = min + (max - min) * bin / count;
                double upper = min + (max - min) * (bin + 1) / count;
                double value = lower + _rng.NextDouble() * (upper - lower);

                candidate[key] = isInt ? Math.Round(value) : value;
            }
            results.Add(candidate);
        }

        return results;
    }

    // ── Kernel Density Estimation ──────────────────────────────────────────

    /// <summary>
    /// Multivariate KDE using product of univariate Gaussian kernels
    /// with Silverman's rule-of-thumb bandwidth.
    /// </summary>
    private double KernelDensity(Dictionary<string, double> point, List<Dictionary<string, double>> observations)
    {
        if (observations.Count == 0) return 1e-12;

        double logDensity = 0;
        foreach (var (key, value) in point)
        {
            if (!_bounds.ContainsKey(key)) continue;

            var values = observations
                .Where(o => o.ContainsKey(key))
                .Select(o => o[key])
                .ToList();

            if (values.Count == 0) continue;

            double bandwidth = SilvermanBandwidth(values) * _bandwidthScale;
            // Floor for constant/near-constant parameters: scale to 1% of the parameter
            // range so the bandwidth is meaningful regardless of parameter scale
            if (bandwidth < 1e-10)
            {
                var (min, max, _) = _bounds[key];
                bandwidth = Math.Max(1e-10, (max - min) * 0.01);
            }

            // Use logsumexp trick for numerical stability: log(Σ exp(xᵢ)) = max(x) + log(Σ exp(xᵢ - max(x)))
            // This avoids underflow when all kernel contributions are very small.
            double logNorm = Math.Log(values.Count * bandwidth * Math.Sqrt(2 * Math.PI));
            var logKernels = new double[values.Count];
            double maxLogK = double.NegativeInfinity;
            for (int k = 0; k < values.Count; k++)
            {
                double z = (value - values[k]) / bandwidth;
                logKernels[k] = -0.5 * z * z;
                if (logKernels[k] > maxLogK) maxLogK = logKernels[k];
            }
            double sumExp = 0;
            for (int k = 0; k < values.Count; k++)
                sumExp += Math.Exp(logKernels[k] - maxLogK);
            double logDim = maxLogK + Math.Log(sumExp) - logNorm;

            logDensity += logDim;
        }

        return Math.Exp(logDensity);
    }

    /// <summary>Samples a point from the KDE of the given observation set.</summary>
    private Dictionary<string, double> SampleFromKde(List<Dictionary<string, double>> observations)
    {
        // Pick a random observation as the kernel center
        var center = observations[_rng.Next(observations.Count)];
        var sample = new Dictionary<string, double>();

        foreach (var (key, (min, max, isInt)) in _bounds)
        {
            double centerVal = center.ContainsKey(key) ? center[key] : (min + max) / 2.0;

            var values = observations
                .Where(o => o.ContainsKey(key))
                .Select(o => o[key])
                .ToList();

            double bandwidth = SilvermanBandwidth(values) * _bandwidthScale;
            if (bandwidth < 1e-10) bandwidth = (max - min) * 0.1;

            // Sample from Gaussian kernel centered at the chosen observation
            double value = centerVal + bandwidth * SampleStandardNormal();
            sample[key] = isInt ? Math.Round(value) : value;
        }

        return sample;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private double SilvermanBandwidth(List<double> values)
    {
        if (values.Count < 2)
        {
            // With a single observation, use 10% of the range as a reasonable default
            // instead of the arbitrary 1.0 which is scale-unaware
            if (values.Count == 1 && _bounds.Count > 0)
            {
                double maxRange = _bounds.Values.Max(b => b.Max - b.Min);
                return Math.Max(1e-6, maxRange * 0.1);
            }
            return 1.0;
        }
        double mean   = values.Average();
        double stdDev = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
        // Silverman's rule: h = 1.06 * sigma * n^(-1/5)
        return 1.06 * stdDev * Math.Pow(values.Count, -0.2);
    }

    private void ClampToBounds(Dictionary<string, double> candidate)
    {
        foreach (var (key, (min, max, isInt)) in _bounds)
        {
            if (!candidate.ContainsKey(key)) continue;
            double val = Math.Clamp(candidate[key], min, max);
            candidate[key] = isInt ? Math.Round(val) : val;
        }
    }

    private double SampleStandardNormal()
    {
        // Box-Muller transform
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private void Shuffle(int[] array)
    {
        for (int i = array.Length - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }
    }
}
