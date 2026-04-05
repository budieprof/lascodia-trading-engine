namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Gaussian Process surrogate model with Upper Confidence Bound (UCB) acquisition function.
/// Preferred over TPE when the parameter space has 6+ dimensions where the KDE-based TPE
/// struggles with the curse of dimensionality. The GP models the objective function as a
/// multivariate normal distribution and proposes candidates where the UCB = μ(x) + β·σ(x)
/// is highest — balancing exploitation (high mean) with exploration (high uncertainty).
///
/// Uses a squared-exponential (RBF) kernel with automatic lengthscale estimation via
/// marginal likelihood maximisation over a grid of candidates.
/// </summary>
internal sealed class GaussianProcessSurrogate
{
    private readonly List<(double[] Params, double Score)> _observations = [];
    private readonly int _dimensions;
    private readonly double[] _lowerBounds;
    private readonly double[] _upperBounds;
    private readonly bool[] _isInteger;
    private readonly string[] _paramNames;
    private readonly double _beta;
    private readonly DeterministicRandom _rng;

    // Kernel hyperparameters
    private double _signalVariance = 1.0;
    private double _lengthscale    = 1.0;
    private double _noiseVariance  = 0.01;

    // Cached Cholesky decomposition of K + σ²I
    private double[,]? _choleskyL;
    private double[]? _alpha; // L⁻¹ · y

    // Tracks how many times the Cholesky diagonal was clamped (indicates ill-conditioning)
    [ThreadStatic] private static int _lastCholeskyClampCount;

    /// <summary>Number of diagonal clamps in the last Cholesky decomposition. Non-zero means the GP
    /// is struggling with correlated or duplicate observations and predictions may be unreliable.</summary>
    public int LastCholeskyClampCount => _lastCholeskyClampCount;

    /// <param name="paramNames">Ordered parameter names.</param>
    /// <param name="lowerBounds">Lower bound per dimension.</param>
    /// <param name="upperBounds">Upper bound per dimension.</param>
    /// <param name="isInteger">Whether each dimension is integer-valued.</param>
    /// <param name="beta">
    /// UCB base exploration coefficient (default 2.0). The effective beta grows as
    /// sqrt(2 * log(t)) where t is the observation count, per GP-UCB theory, to
    /// balance exploration/exploitation dynamically as the search progresses.
    /// </param>
    /// <param name="seed">Random seed for reproducibility.</param>
    public GaussianProcessSurrogate(
        string[] paramNames,
        double[] lowerBounds,
        double[] upperBounds,
        bool[] isInteger,
        double beta = 2.0,
        int seed = 42,
        ulong? randomState = null)
    {
        _paramNames  = paramNames;
        _dimensions  = paramNames.Length;
        _lowerBounds = lowerBounds;
        _upperBounds = upperBounds;
        _isInteger   = isInteger;
        _beta        = beta;
        _rng         = randomState.HasValue ? new DeterministicRandom(randomState.Value) : new DeterministicRandom(seed);
    }

    private int _suggestCallCount;
    private int _lastFitObsCount;

    /// <summary>
    /// Maximum observations used for GP kernel operations. Beyond this, the surrogate
    /// auto-downsamples to the most informative subset (recent + best scoring) to keep
    /// Cholesky decomposition O(n^3) tractable. Default 200, which gives ~8M flops per
    /// decomposition — acceptable within a 30s timeout budget.
    /// </summary>
    private const int MaxActiveObservations = 200;

    public int ObservationCount => _observations.Count;
    public ulong RandomState => _rng.State;
    internal int Dimensions => _dimensions;
    internal string[] ParamNames => _paramNames;

    /// <summary>
    /// Predicts mean and variance for a candidate specified as a parameter dictionary.
    /// Ensures the GP is fitted before prediction. Used by EHVI acquisition.
    /// </summary>
    internal (double Mean, double Variance) PredictFromParams(Dictionary<string, double> parameters)
    {
        var point = new double[_dimensions];
        for (int i = 0; i < _dimensions; i++)
            point[i] = parameters.TryGetValue(_paramNames[i], out double v) ? Normalise(v, i) : 0.5;
        EnsureFitted();
        return Predict(point);
    }

    /// <summary>Ensures hyperparameters are fitted and Cholesky cache is built.</summary>
    internal void EnsureFitted()
    {
        if (_observations.Count >= 10 && (_choleskyL is null || _alpha is null))
        {
            FitHyperparameters();
            _lastFitObsCount = _observations.Count;
        }
        BuildCholeskyCache();
    }

    /// <summary>Records a completed evaluation.</summary>
    public void AddObservation(Dictionary<string, double> parameters, double score)
        => AddObservation(parameters, score, weight: 1.0);

    /// <summary>
    /// Records a completed evaluation with a weight. Weights &lt; 1.0 reduce the
    /// observation's influence on the GP posterior — used for non-current-regime
    /// warm-start observations that may not reflect current market conditions.
    /// Weight is applied by scaling the score toward the mean.
    /// </summary>
    public void AddObservation(Dictionary<string, double> parameters, double score, double weight)
    {
        var point = new double[_dimensions];
        for (int i = 0; i < _dimensions; i++)
            point[i] = parameters.TryGetValue(_paramNames[i], out double v) ? Normalise(v, i) : 0.5;

        // Weight < 1.0: blend score toward the current observation mean to reduce influence.
        // This is simpler than modifying the noise kernel per observation (which would
        // require heteroscedastic GP) and achieves the same effect for warm-start use.
        if (weight < 1.0 && weight > 0.0 && _observations.Count > 0)
        {
            double mean = _observations.Average(o => o.Score);
            score = mean + (score - mean) * weight;
        }

        _observations.Add((point, score));
        _choleskyL = null; // Invalidate cache
        _alpha     = null;
        _lastActiveCount = 0; // Invalidate active subset
    }

    /// <summary>
    /// Proposes the next batch of candidates using UCB acquisition.
    /// Falls back to Latin Hypercube Sampling when fewer than <paramref name="minForModel"/>
    /// observations are available.
    /// </summary>
    public List<Dictionary<string, double>> SuggestCandidates(
        int count,
        int minForModel = 10,
        int randomCandidates = 500)
    {
        if (_observations.Count < minForModel)
            return LatinHypercubeSample(count);

        // Only refit hyperparameters every 5th call or when observation count has grown
        // significantly. The 240-combo grid search is expensive (O(240 × n³)) so throttling
        // prevents it from dominating total optimization time for large observation sets.
        _suggestCallCount++;
        if (_suggestCallCount % 5 == 1 || _observations.Count > _lastFitObsCount + 4)
        {
            FitHyperparameters();
            _lastFitObsCount = _observations.Count;
        }
        BuildCholeskyCache();

        var candidates = new List<(Dictionary<string, double> Params, double UCB)>();

        for (int i = 0; i < randomCandidates; i++)
        {
            var x = RandomNormalisedPoint();
            var (mean, variance) = Predict(x);
            // Adaptive beta: grows with observation count per GP-UCB theory
            double adaptiveBeta = _beta * Math.Sqrt(2.0 * Math.Log(Math.Max(1, _observations.Count)));
            double ucb = mean + adaptiveBeta * Math.Sqrt(Math.Max(0, variance));
            candidates.Add((Denormalise(x), ucb));
        }

        // Acquisition diversity: reserve 20% of slots for pure random exploration
        // to prevent surrogate tunnel vision in deceptive landscapes
        int exploitCount = Math.Max(1, (int)(count * 0.8));
        int exploreCount = count - exploitCount;

        var result = candidates
            .OrderByDescending(c => c.UCB)
            .Take(exploitCount)
            .Select(c => c.Params)
            .ToList();

        if (exploreCount > 0)
            result.AddRange(LatinHypercubeSample(exploreCount));

        return result;
    }

    // ── Observation downsampling ──────────────────────────────────────────

    /// <summary>
    /// Returns the active observation subset used for GP kernel operations. When total
    /// observations exceed <see cref="MaxActiveObservations"/>, selects a mix of the
    /// highest-scoring observations (exploitation memory) and most recent observations
    /// (exploration recency) to maintain both quality and coverage while keeping O(n^3)
    /// Cholesky tractable. All observations are still stored for surrogate warm-start.
    /// </summary>
    private List<(double[] Params, double Score)> _activeObservations = [];
    private int _lastActiveCount;

    private List<(double[] Params, double Score)> GetActiveObservations()
    {
        if (_observations.Count <= MaxActiveObservations)
            return _observations;

        // Rebuild active set only when observation count has changed
        if (_lastActiveCount == _observations.Count && _activeObservations.Count > 0)
            return _activeObservations;

        _lastActiveCount = _observations.Count;

        // Strategy: 60% best-scoring (exploitation memory), 40% most recent (exploration recency).
        // Dedup by index to avoid counting the same observation twice.
        int bestCount   = (int)(MaxActiveObservations * 0.6);
        int recentCount = MaxActiveObservations - bestCount;

        var bestIndices = Enumerable.Range(0, _observations.Count)
            .OrderByDescending(i => _observations[i].Score)
            .Take(bestCount)
            .ToHashSet();

        var recentIndices = Enumerable.Range(0, _observations.Count)
            .Reverse()
            .Where(i => !bestIndices.Contains(i))
            .Take(recentCount);

        var selectedIndices = bestIndices.Concat(recentIndices).OrderBy(i => i).ToList();
        _activeObservations = selectedIndices.Select(i => _observations[i]).ToList();
        return _activeObservations;
    }

    // ── GP Core ─────────────────────────────────────────────────────────────

    /// <summary>Squared-exponential (RBF) kernel.</summary>
    private double Kernel(double[] a, double[] b)
    {
        double sq = 0;
        for (int i = 0; i < _dimensions; i++)
        {
            double diff = a[i] - b[i];
            sq += diff * diff;
        }
        return _signalVariance * Math.Exp(-0.5 * sq / (_lengthscale * _lengthscale));
    }

    /// <summary>Predicts mean and variance at a new point using the GP posterior.</summary>
    internal (double Mean, double Variance) Predict(double[] x)
    {
        if (_choleskyL is null || _alpha is null) return (0, _signalVariance);

        var active = GetActiveObservations();
        int n      = active.Count;
        var kStar  = new double[n];
        for (int i = 0; i < n; i++)
            kStar[i] = Kernel(x, active[i].Params);

        // Mean = k*ᵀ · α
        double mean = 0;
        for (int i = 0; i < n; i++)
            mean += kStar[i] * _alpha[i];

        // Variance = k(x,x) - k*ᵀ · (K + σ²I)⁻¹ · k* = k(x,x) - v·v where v = L⁻¹·k*
        var v = SolveLowerTriangular(_choleskyL, kStar);
        double vDotV = 0;
        for (int i = 0; i < n; i++)
            vDotV += v[i] * v[i];

        double variance = _signalVariance - vDotV;
        return (mean, Math.Max(0, variance));
    }

    /// <summary>Builds the Cholesky decomposition of K + σ²I and solves for α = (K+σ²I)⁻¹y.</summary>
    internal void BuildCholeskyCache()
    {
        var active = GetActiveObservations();
        int n = active.Count;
        if (n == 0) return;

        var K = new double[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j <= i; j++)
        {
            double k = Kernel(active[i].Params, active[j].Params);
            if (i == j) k += _noiseVariance;
            K[i, j] = k;
            K[j, i] = k;
        }

        _choleskyL = CholeskyDecompose(K, n);

        // Solve Lz = y for z, then Lᵀα = z for α
        var y = active.Select(o => o.Score).ToArray();
        var z = SolveLowerTriangular(_choleskyL, y);
        _alpha = SolveUpperTriangular(_choleskyL, z);
    }

    /// <summary>
    /// Fits lengthscale, signal variance, and noise variance by evaluating marginal
    /// log-likelihood over a grid and selecting the best combination.
    /// The grid is denser than a naive 3-value sweep to avoid missing good hyperparameters
    /// in higher-dimensional spaces.
    /// </summary>
    private void FitHyperparameters()
    {
        if (_observations.Count < 5) return;

        double bestLogLik    = double.NegativeInfinity;
        double bestLength    = _lengthscale;
        double bestSignalVar = _signalVariance;
        double bestNoiseVar  = _noiseVariance;

        // Expanded grid: 10 lengthscales × 6 signal variances × 4 noise variances = 240 combos
        foreach (double ls in new[] { 0.05, 0.1, 0.2, 0.3, 0.5, 0.7, 1.0, 1.5, 2.0, 3.0 })
        foreach (double sv in new[] { 0.1, 0.3, 0.5, 1.0, 2.0, 4.0 })
        foreach (double nv in new[] { 0.001, 0.01, 0.05, 0.1 })
        {
            _lengthscale    = ls;
            _signalVariance = sv;
            _noiseVariance  = nv;

            try
            {
                BuildCholeskyCache();
                double logLik = ComputeLogMarginalLikelihood();
                if (logLik > bestLogLik)
                {
                    bestLogLik    = logLik;
                    bestLength    = ls;
                    bestSignalVar = sv;
                    bestNoiseVar  = nv;
                }
            }
            catch { /* singular matrix at these hyperparams — skip */ }
        }

        _lengthscale    = bestLength;
        _signalVariance = bestSignalVar;
        _noiseVariance  = bestNoiseVar;

        // Invalidate Cholesky cache: the grid search left _choleskyL/_alpha set to the
        // last grid candidate evaluated (which may not be the best). Force a rebuild
        // with the best hyperparameters on the next call to BuildCholeskyCache().
        _choleskyL = null;
        _alpha     = null;
    }

    private double ComputeLogMarginalLikelihood()
    {
        if (_choleskyL is null || _alpha is null) return double.NegativeInfinity;
        int n = _observations.Count;
        var y = _observations.Select(o => o.Score).ToArray();

        // log p(y|X,θ) = -0.5·yᵀα - Σlog(Lii) - (n/2)·log(2π)
        double dataFit = 0;
        for (int i = 0; i < n; i++)
            dataFit += y[i] * _alpha[i];

        double complexity = 0;
        for (int i = 0; i < n; i++)
            complexity += Math.Log(_choleskyL[i, i]);

        return -0.5 * dataFit - complexity - 0.5 * n * Math.Log(2 * Math.PI);
    }

    // ── Linear algebra helpers ──────────────────────────────────────────────

    private static double[,] CholeskyDecompose(double[,] A, int n)
    {
        _lastCholeskyClampCount = 0;
        var L = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = 0;
                for (int k = 0; k < j; k++)
                    sum += L[i, k] * L[j, k];

                if (i == j)
                {
                    double diag = A[i, i] - sum;
                    if (diag <= 0)
                    {
                        diag = 1e-10; // Numerical stabilisation for near-singular matrix
                        _lastCholeskyClampCount++;
                    }
                    L[i, j] = Math.Sqrt(diag);
                }
                else
                {
                    L[i, j] = (A[i, j] - sum) / L[j, j];
                }
            }
        }
        return L;
    }

    /// <summary>Solves Lx = b where L is lower triangular.</summary>
    private static double[] SolveLowerTriangular(double[,] L, double[] b)
    {
        int n = b.Length;
        var x = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int j = 0; j < i; j++)
                sum += L[i, j] * x[j];
            x[i] = (b[i] - sum) / L[i, i];
        }
        return x;
    }

    /// <summary>Solves Lᵀx = b where L is lower triangular.</summary>
    private static double[] SolveUpperTriangular(double[,] L, double[] b)
    {
        int n = b.Length;
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = 0;
            for (int j = i + 1; j < n; j++)
                sum += L[j, i] * x[j]; // L[j,i] = Lᵀ[i,j]
            x[i] = (b[i] - sum) / L[i, i];
        }
        return x;
    }

    // ── Normalisation / sampling ────────────────────────────────────────────

    internal double Normalise(double value, int dim)
    {
        double range = _upperBounds[dim] - _lowerBounds[dim];
        return range > 0 ? (value - _lowerBounds[dim]) / range : 0.5;
    }

    private double Denormalise(double normalised, int dim)
    {
        double range = _upperBounds[dim] - _lowerBounds[dim];
        double val   = _lowerBounds[dim] + normalised * range;
        return _isInteger[dim] ? Math.Round(val) : val;
    }

    internal Dictionary<string, double> Denormalise(double[] normPoint)
    {
        var result = new Dictionary<string, double>();
        for (int i = 0; i < _dimensions; i++)
            result[_paramNames[i]] = Denormalise(normPoint[i], i);
        return result;
    }

    internal double[] RandomNormalisedPoint()
    {
        var x = new double[_dimensions];
        for (int i = 0; i < _dimensions; i++)
            x[i] = _rng.NextDouble();
        return x;
    }

    private List<Dictionary<string, double>> LatinHypercubeSample(int count)
    {
        var results      = new List<Dictionary<string, double>>();
        var permutations = new int[_dimensions][];

        for (int d = 0; d < _dimensions; d++)
        {
            permutations[d] = Enumerable.Range(0, count).ToArray();
            for (int i = permutations[d].Length - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (permutations[d][i], permutations[d][j]) = (permutations[d][j], permutations[d][i]);
            }
        }

        for (int i = 0; i < count; i++)
        {
            var candidate = new Dictionary<string, double>();
            for (int d = 0; d < _dimensions; d++)
            {
                double lower = (double)permutations[d][i] / count;
                double upper = (double)(permutations[d][i] + 1) / count;
                double norm  = lower + _rng.NextDouble() * (upper - lower);
                candidate[_paramNames[d]] = Denormalise(norm, d);
            }
            results.Add(candidate);
        }

        return results;
    }
}
