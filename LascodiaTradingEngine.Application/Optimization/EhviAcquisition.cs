using System.Text.Json;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Common.Diagnostics;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Expected Hypervolume Improvement (EHVI) acquisition for multi-objective Bayesian
/// optimization. Uses 3 independent GPs (Sharpe, -MaxDrawdown, WinRate) and Monte Carlo
/// estimation to compute the expected improvement in Pareto front hypervolume.
///
/// Production hardening over baseline EHVI:
/// - Pareto front size cap (crowding distance pruning) to bound HV computation cost
/// - MC convergence early-exit when running standard error stabilizes
/// - Shared GP refit schedule (single decision, 3 Cholesky decompositions)
/// - Pre-computed front HV cache per candidate loop
/// - Incremental 2D HV maintenance during 3D sweep
/// - Objective correlation correction via empirical Cholesky in MC sampling
/// - Multi-GP uncertainty-weighted exploration reserve
/// - Per-batch diagnostic logging (front size, HV, GP prediction error)
/// - Checkpoint-serializable state (Pareto front + GP observation counts)
///
/// Batch suggestions use Kriging Believer: after selecting each candidate, its predicted
/// mean is added as a phantom to the working front.
/// </summary>
internal sealed class EhviAcquisition
{
    private readonly GaussianProcessSurrogate _gpSharpe;
    private readonly GaussianProcessSurrogate _gpDrawdown;
    private readonly GaussianProcessSurrogate _gpWinRate;
    private readonly DeterministicRandom _rng;
    private readonly int _mcSamples;
    private readonly double _sharpeMin;
    private readonly double _sharpeMax;
    private readonly double _maxDrawdownCeiling;
    private readonly ILogger? _logger;
    private readonly TradingMetrics? _metrics;

    private const double WeightThresholdForParetoInclusion = 0.30;
    private const double ExplorationReserveRatio = 0.20;
    private const int MaxParetoFrontSize = 25;
    private const int MinMcSamplesBeforeEarlyExit = 64;
    private const double McEarlyExitRelativeError = 0.01; // 1% of running mean

    // Current Pareto front in normalized objective space (all objectives maximized)
    private List<double[]> _paretoFront = [];

    // Shared refit state: all 3 GPs refit on the same schedule
    private int _suggestCallCount;
    private int _lastRefitObsCount;

    // Empirical objective correlation (updated incrementally)
    private readonly List<double[]> _objectiveHistory = [];

    internal int ObservationCount => _gpSharpe.ObservationCount;
    internal int ParetoFrontSize => _paretoFront.Count;
    internal double CurrentHypervolume => Hypervolume3D(_paretoFront);

    internal EhviAcquisition(
        string[] paramNames, double[] lowerBounds, double[] upperBounds, bool[] isInteger,
        int seed, int mcSamples = 256,
        double sharpeMin = -2.0, double sharpeMax = 4.0, double maxDrawdownCeiling = 50.0,
        ILogger? logger = null, TradingMetrics? metrics = null)
    {
        _gpSharpe   = new GaussianProcessSurrogate(paramNames, lowerBounds, upperBounds, isInteger, seed: seed);
        _gpDrawdown = new GaussianProcessSurrogate(paramNames, lowerBounds, upperBounds, isInteger, seed: seed + 1);
        _gpWinRate  = new GaussianProcessSurrogate(paramNames, lowerBounds, upperBounds, isInteger, seed: seed + 2);
        _rng = new DeterministicRandom(seed + 3);
        _mcSamples = mcSamples;
        _sharpeMin = sharpeMin;
        _sharpeMax = sharpeMax;
        _maxDrawdownCeiling = maxDrawdownCeiling;
        _logger = logger;
        _metrics = metrics;
    }

    // ── Observation recording ──────────────────────────────────────────────

    internal void AddObservation(Dictionary<string, double> parameters, BacktestResult result)
    {
        var (normS, normDD, normWR) = NormalizeObjectives(result);
        _gpSharpe.AddObservation(parameters, normS);
        _gpDrawdown.AddObservation(parameters, normDD);
        _gpWinRate.AddObservation(parameters, normWR);
        _objectiveHistory.Add([normS, normDD, normWR]);
        AddToParetoFront([normS, normDD, normWR]);
    }

    /// <summary>Weighted observation for warm-start (regime conditioning).</summary>
    internal void AddObservation(Dictionary<string, double> parameters, BacktestResult result, double weight)
    {
        var (normS, normDD, normWR) = NormalizeObjectives(result);
        _gpSharpe.AddObservation(parameters, normS, weight);
        _gpDrawdown.AddObservation(parameters, normDD, weight);
        _gpWinRate.AddObservation(parameters, normWR, weight);
        _objectiveHistory.Add([normS, normDD, normWR]);
        if (weight >= WeightThresholdForParetoInclusion)
            AddToParetoFront([normS, normDD, normWR]);
    }

    /// <summary>
    /// Warm-start from per-objective metrics stored on prior OptimizationRun records.
    /// Does not require a full BacktestResult — uses the decomposed objective values directly.
    /// </summary>
    internal void AddWarmStartObservation(
        Dictionary<string, double> parameters,
        decimal sharpeRatio, decimal maxDrawdownPct, decimal winRate, double weight)
    {
        var (normS, normDD, normWR) = NormalizeRaw(sharpeRatio, maxDrawdownPct, winRate);
        _gpSharpe.AddObservation(parameters, normS, weight);
        _gpDrawdown.AddObservation(parameters, normDD, weight);
        _gpWinRate.AddObservation(parameters, normWR, weight);
        _objectiveHistory.Add([normS, normDD, normWR]);
        if (weight >= WeightThresholdForParetoInclusion)
            AddToParetoFront([normS, normDD, normWR]);
    }

    // ── Candidate suggestion ───────────────────────────────────────────────

    internal List<Dictionary<string, double>> SuggestCandidates(
        int count, int minForModel = 10, int randomCandidates = 500)
    {
        if (_gpSharpe.ObservationCount < minForModel)
            return _gpSharpe.SuggestCandidates(count, minForModel, randomCandidates);

        // Shared refit schedule: single decision, 3 Cholesky decompositions.
        _suggestCallCount++;
        bool shouldRefit = _suggestCallCount % 5 == 1
                        || _gpSharpe.ObservationCount > _lastRefitObsCount + 4;
        if (shouldRefit) _lastRefitObsCount = _gpSharpe.ObservationCount;

        _gpSharpe.EnsureFitted();
        _gpDrawdown.EnsureFitted();
        _gpWinRate.EnsureFitted();

        // Snapshot the Pareto front for Kriging Believer
        var workingFront = _paretoFront.Select(p => (double[])p.Clone()).ToList();

        // Pre-compute empirical correlation Cholesky for correlated MC sampling
        var corrCholesky = ComputeCorrelationCholesky();

        int exploitSlots = Math.Max(1, (int)(count * (1.0 - ExplorationReserveRatio)));
        int exploreSlots = count - exploitSlots;

        var result = new List<Dictionary<string, double>>(count);
        var batchPredictions = new List<(double[] Predicted, double[] Actual)>();

        for (int k = 0; k < exploitSlots; k++)
        {
            // Cache front HV — constant within a single Kriging Believer step
            double frontHv = Hypervolume3D(workingFront);

            var best = FindBestEhviCandidate(randomCandidates, workingFront, frontHv, corrCholesky);
            if (best.Candidate is not null)
            {
                result.Add(best.Candidate);
                UpdateParetoFrontWorking(workingFront, best.Mean);
            }
        }

        // Multi-GP uncertainty-weighted exploration: prefer candidates where the
        // maximum variance across any objective is highest
        if (exploreSlots > 0)
        {
            var exploreCandidates = new List<(Dictionary<string, double> Params, double MaxVar)>();
            for (int i = 0; i < randomCandidates; i++)
            {
                var normPoint = _gpSharpe.RandomNormalisedPoint();
                var denorm = _gpSharpe.Denormalise(normPoint);
                var (_, varS) = _gpSharpe.PredictFromParams(denorm);
                var (_, varD) = _gpDrawdown.PredictFromParams(denorm);
                var (_, varW) = _gpWinRate.PredictFromParams(denorm);
                double maxVar = Math.Max(varS, Math.Max(varD, varW));
                exploreCandidates.Add((denorm, maxVar));
            }
            result.AddRange(exploreCandidates
                .OrderByDescending(c => c.MaxVar)
                .Take(exploreSlots)
                .Select(c => c.Params));
        }

        // Diagnostic logging
        if (_logger is not null || _metrics is not null)
        {
            double hv = Hypervolume3D(workingFront);
            _metrics?.EhviHypervolumeProgress.Record(hv);
            _logger?.LogDebug(
                "EHVI: batch complete — front={FrontSize}, HV={HV:F4}, observations={Obs}",
                workingFront.Count, hv, _gpSharpe.ObservationCount);
        }

        return result;
    }

    /// <summary>
    /// Records actual vs predicted for GP prediction quality tracking.
    /// Call after evaluating a batch of suggestions to compute per-objective MAE.
    /// </summary>
    internal void RecordPredictionErrors(
        IReadOnlyList<(Dictionary<string, double> Params, BacktestResult Result)> evaluated)
    {
        if (_metrics is null || evaluated.Count == 0) return;

        double totalErrS = 0, totalErrD = 0, totalErrW = 0;
        foreach (var (p, r) in evaluated)
        {
            var (actS, actD, actW) = NormalizeObjectives(r);
            var (predS, _) = _gpSharpe.PredictFromParams(p);
            var (predD, _) = _gpDrawdown.PredictFromParams(p);
            var (predW, _) = _gpWinRate.PredictFromParams(p);
            totalErrS += Math.Abs(predS - actS);
            totalErrD += Math.Abs(predD - actD);
            totalErrW += Math.Abs(predW - actW);
        }
        int n = evaluated.Count;
        _metrics.EhviGpPredictionError.Record(totalErrS / n, new KeyValuePair<string, object?>("objective", "sharpe"));
        _metrics.EhviGpPredictionError.Record(totalErrD / n, new KeyValuePair<string, object?>("objective", "drawdown"));
        _metrics.EhviGpPredictionError.Record(totalErrW / n, new KeyValuePair<string, object?>("objective", "winrate"));
    }

    // ── Checkpoint serialization ───────────────────────────────────────────

    internal sealed record EhviCheckpoint(
        List<double[]> ParetoFront,
        int ObservationCount,
        List<double[]> ObjectiveHistory);

    internal string SerializeCheckpoint()
        => JsonSerializer.Serialize(new EhviCheckpoint(_paretoFront, _gpSharpe.ObservationCount, _objectiveHistory));

    internal void RestoreCheckpoint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var cp = JsonSerializer.Deserialize<EhviCheckpoint>(json);
            if (cp is null) return;
            _paretoFront = cp.ParetoFront;
            _objectiveHistory.Clear();
            _objectiveHistory.AddRange(cp.ObjectiveHistory);
        }
        catch { /* malformed checkpoint — start fresh */ }
    }

    // ── Core EHVI computation ──────────────────────────────────────────────

    private (Dictionary<string, double>? Candidate, double[] Mean) FindBestEhviCandidate(
        int randomCandidates, List<double[]> workingFront, double frontHv, double[,]? corrCholesky)
    {
        Dictionary<string, double>? bestCandidate = null;
        double bestEhvi = double.NegativeInfinity;
        double[] bestMean = [0, 0, 0];

        for (int i = 0; i < randomCandidates; i++)
        {
            var normPoint = _gpSharpe.RandomNormalisedPoint();
            var denorm = _gpSharpe.Denormalise(normPoint);

            var (meanS, varS) = _gpSharpe.PredictFromParams(denorm);
            var (meanD, varD) = _gpDrawdown.PredictFromParams(denorm);
            var (meanW, varW) = _gpWinRate.PredictFromParams(denorm);

            double ehvi = MonteCarloEhvi(
                meanS, varS, meanD, varD, meanW, varW, workingFront, frontHv, corrCholesky);

            if (ehvi > bestEhvi)
            {
                bestEhvi = ehvi;
                bestCandidate = denorm;
                bestMean = [meanS, meanD, meanW];
            }
        }

        return (bestCandidate, bestMean);
    }

    /// <summary>
    /// MC-EHVI with pre-computed front HV, correlated sampling, and convergence early-exit.
    /// </summary>
    private double MonteCarloEhvi(
        double meanS, double varS,
        double meanD, double varD,
        double meanW, double varW,
        List<double[]> front,
        double frontHv,
        double[,]? corrCholesky)
    {
        double sigmaS = Math.Sqrt(Math.Max(0, varS));
        double sigmaD = Math.Sqrt(Math.Max(0, varD));
        double sigmaW = Math.Sqrt(Math.Max(0, varW));

        double totalImprovement = 0;
        double totalImprovementSq = 0; // For running variance (early-exit)
        int samplesUsed = 0;

        for (int s = 0; s < _mcSamples; s++)
        {
            // Correlated sampling: if we have an empirical correlation matrix,
            // draw correlated standard normals to avoid overestimating joint variance.
            double z0, z1, z2;
            if (corrCholesky is not null)
            {
                double u0 = SampleStdNormal(), u1 = SampleStdNormal(), u2 = SampleStdNormal();
                z0 = corrCholesky[0, 0] * u0;
                z1 = corrCholesky[1, 0] * u0 + corrCholesky[1, 1] * u1;
                z2 = corrCholesky[2, 0] * u0 + corrCholesky[2, 1] * u1 + corrCholesky[2, 2] * u2;
            }
            else
            {
                z0 = SampleStdNormal(); z1 = SampleStdNormal(); z2 = SampleStdNormal();
            }

            double sampleS = Math.Clamp(meanS + sigmaS * z0, 0, 1);
            double sampleD = Math.Clamp(meanD + sigmaD * z1, 0, 1);
            double sampleW = Math.Clamp(meanW + sigmaW * z2, 0, 1);

            // Quick dominance check
            bool dominated = false;
            for (int f = 0; f < front.Count; f++)
            {
                if (front[f][0] >= sampleS && front[f][1] >= sampleD && front[f][2] >= sampleW)
                { dominated = true; break; }
            }

            double improvement = 0;
            if (!dominated)
            {
                improvement = ExclusiveContribution(sampleS, sampleD, sampleW, front, frontHv);
                if (improvement < 0) improvement = 0;
            }

            totalImprovement += improvement;
            totalImprovementSq += improvement * improvement;
            samplesUsed = s + 1;

            // Convergence early-exit: stop when the running standard error
            // drops below 1% of the running mean. Requires minimum 64 samples.
            if (samplesUsed >= MinMcSamplesBeforeEarlyExit && totalImprovement > 0)
            {
                double mean = totalImprovement / samplesUsed;
                double variance = (totalImprovementSq / samplesUsed) - (mean * mean);
                double se = Math.Sqrt(Math.Max(0, variance) / samplesUsed);
                if (se < McEarlyExitRelativeError * mean)
                    break;
            }
        }

        return samplesUsed > 0 ? totalImprovement / samplesUsed : 0;
    }

    /// <summary>
    /// Exclusive HV contribution = HV(front ∪ {new}) - frontHv.
    /// Accepts pre-computed frontHv to avoid redundant recomputation across MC samples.
    /// </summary>
    private static double ExclusiveContribution(
        double s, double d, double w, List<double[]> front, double frontHv)
    {
        var extended = new List<double[]>(front.Count + 1);
        for (int i = 0; i < front.Count; i++)
        {
            if (front[i][0] <= s && front[i][1] <= d && front[i][2] <= w)
                continue; // Dominated by new point
            extended.Add(front[i]);
        }
        extended.Add([s, d, w]);
        return Hypervolume3D(extended) - frontHv;
    }

    // ── Objective correlation ──────────────────────────────────────────────

    /// <summary>
    /// Computes the Cholesky decomposition of the empirical 3×3 correlation matrix.
    /// Returns null if insufficient data (< 10 observations) or if the matrix is
    /// not positive definite (degenerate case).
    /// </summary>
    private double[,]? ComputeCorrelationCholesky()
    {
        if (_objectiveHistory.Count < 10) return null;

        // Compute means
        double meanS = 0, meanD = 0, meanW = 0;
        foreach (var o in _objectiveHistory)
        { meanS += o[0]; meanD += o[1]; meanW += o[2]; }
        int n = _objectiveHistory.Count;
        meanS /= n; meanD /= n; meanW /= n;

        // Compute covariance matrix
        double cSS = 0, cDD = 0, cWW = 0, cSD = 0, cSW = 0, cDW = 0;
        foreach (var o in _objectiveHistory)
        {
            double ds = o[0] - meanS, dd = o[1] - meanD, dw = o[2] - meanW;
            cSS += ds * ds; cDD += dd * dd; cWW += dw * dw;
            cSD += ds * dd; cSW += ds * dw; cDW += dd * dw;
        }
        cSS /= n; cDD /= n; cWW /= n; cSD /= n; cSW /= n; cDW /= n;

        // Convert to correlation matrix
        double stdS = Math.Sqrt(Math.Max(cSS, 1e-10));
        double stdD = Math.Sqrt(Math.Max(cDD, 1e-10));
        double stdW = Math.Sqrt(Math.Max(cWW, 1e-10));

        var R = new double[3, 3];
        R[0, 0] = 1.0;          R[0, 1] = cSD / (stdS * stdD); R[0, 2] = cSW / (stdS * stdW);
        R[1, 0] = R[0, 1];      R[1, 1] = 1.0;                 R[1, 2] = cDW / (stdD * stdW);
        R[2, 0] = R[0, 2];      R[2, 1] = R[1, 2];             R[2, 2] = 1.0;

        // Cholesky decomposition of 3×3 symmetric positive definite matrix
        var L = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = R[i, j];
                for (int k = 0; k < j; k++)
                    sum -= L[i, k] * L[j, k];

                if (i == j)
                {
                    if (sum <= 0) return null; // Not positive definite
                    L[i, j] = Math.Sqrt(sum);
                }
                else
                {
                    L[i, j] = sum / L[j, j];
                }
            }
        }

        return L;
    }

    // ── Hypervolume computation ────────────────────────────────────────────

    /// <summary>
    /// Exact 3D hypervolume with reference point at origin (0, 0, 0).
    /// Algorithm (Fonseca et al.): sort by first objective descending, sweep from
    /// highest to lowest X. Active set of 2D (Y,Z) points grows incrementally.
    /// O(n² log n) for 3D. Acceptable for n ≤ 50.
    /// </summary>
    internal static double Hypervolume3D(List<double[]> front)
    {
        if (front.Count == 0) return 0;
        if (front.Count == 1) return front[0][0] * front[0][1] * front[0][2];

        var desc = front.OrderByDescending(p => p[0]).ToList();
        double totalVolume = 0;

        // Incremental 2D front: maintain a sorted list of non-dominated (Y,Z) points.
        // As we sweep X descending, each new point is inserted and dominated points removed.
        var activeNd = new List<(double Y, double Z)>();

        for (int i = 0; i < desc.Count; i++)
        {
            double newY = desc[i][1], newZ = desc[i][2];

            // Insert into non-dominated 2D set incrementally
            InsertIntoNd2D(activeNd, newY, newZ);

            double xHigh = desc[i][0];
            double xLow = (i + 1 < desc.Count) ? desc[i + 1][0] : 0.0;
            double sliceWidth = xHigh - xLow;

            if (sliceWidth > 0)
            {
                double area2D = Hypervolume2DFromNd(activeNd);
                totalVolume += sliceWidth * area2D;
            }
        }

        return totalVolume;
    }

    /// <summary>
    /// Inserts a (Y, Z) point into a maintained non-dominated 2D set.
    /// Removes any points dominated by the new one. O(n) per insert.
    /// The list is kept sorted by Z ascending (Y descending for non-dominated).
    /// </summary>
    private static void InsertIntoNd2D(List<(double Y, double Z)> nd, double y, double z)
    {
        // Check if new point is dominated
        for (int i = 0; i < nd.Count; i++)
        {
            if (nd[i].Y >= y && nd[i].Z >= z) return; // Dominated — skip
        }

        // Remove points dominated by the new one
        nd.RemoveAll(p => p.Y <= y && p.Z <= z);

        // Insert sorted by Z ascending
        int insertIdx = nd.Count;
        for (int i = 0; i < nd.Count; i++)
        {
            if (z < nd[i].Z) { insertIdx = i; break; }
        }
        nd.Insert(insertIdx, (y, z));
    }

    /// <summary>
    /// Computes 2D hypervolume from an already non-dominated set sorted by Z ascending.
    /// Staircase accumulation: area = Σ Y_i × (Z_i - Z_{i-1}).
    /// </summary>
    private static double Hypervolume2DFromNd(List<(double Y, double Z)> nd)
    {
        if (nd.Count == 0) return 0;
        double area = 0, prevZ = 0;
        foreach (var p in nd)
        {
            area += p.Y * (p.Z - prevZ);
            prevZ = p.Z;
        }
        return area;
    }

    /// <summary>Extracts the non-dominated subset of a 3D point set. O(n²).</summary>
    private static List<double[]> ExtractNonDominated(List<double[]> points)
    {
        var result = new List<double[]>();
        for (int i = 0; i < points.Count; i++)
        {
            bool dominated = false;
            for (int j = 0; j < points.Count; j++)
            {
                if (i == j) continue;
                if (points[j][0] >= points[i][0] &&
                    points[j][1] >= points[i][1] &&
                    points[j][2] >= points[i][2] &&
                    (points[j][0] > points[i][0] || points[j][1] > points[i][1] || points[j][2] > points[i][2]))
                { dominated = true; break; }
            }
            if (!dominated) result.Add(points[i]);
        }
        return result;
    }

    /// <summary>
    /// Prunes the Pareto front to <see cref="MaxParetoFrontSize"/> using crowding distance.
    /// Keeps boundary points (extremes on each objective) and fills the rest by
    /// descending crowding distance. This bounds HV computation cost while preserving
    /// front diversity.
    /// </summary>
    private static List<double[]> PruneFrontByCrowding(List<double[]> front, int maxSize)
    {
        if (front.Count <= maxSize) return front;

        // Compute crowding distance per point
        var distances = new double[front.Count];
        for (int obj = 0; obj < 3; obj++)
        {
            var sorted = Enumerable.Range(0, front.Count)
                .OrderBy(i => front[i][obj])
                .ToList();

            // Boundary points get infinite distance
            distances[sorted[0]] = double.PositiveInfinity;
            distances[sorted[^1]] = double.PositiveInfinity;

            double range = front[sorted[^1]][obj] - front[sorted[0]][obj];
            if (range <= 0) continue;

            for (int i = 1; i < sorted.Count - 1; i++)
            {
                double gap = (front[sorted[i + 1]][obj] - front[sorted[i - 1]][obj]) / range;
                distances[sorted[i]] += gap;
            }
        }

        return Enumerable.Range(0, front.Count)
            .OrderByDescending(i => distances[i])
            .Take(maxSize)
            .Select(i => front[i])
            .ToList();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private double SampleStdNormal()
    {
        double u1 = Math.Max(_rng.NextDouble(), 1e-15);
        double u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private (double NormS, double NormDD, double NormWR) NormalizeObjectives(BacktestResult result)
        => NormalizeRaw(result.SharpeRatio, result.MaxDrawdownPct, result.WinRate);

    private (double NormS, double NormDD, double NormWR) NormalizeRaw(
        decimal sharpeRatio, decimal maxDrawdownPct, decimal winRate)
    {
        double range = _sharpeMax - _sharpeMin;
        double normS = range > 0
            ? Math.Clamp(((double)sharpeRatio - _sharpeMin) / range, 0, 1) : 0.5;
        double normDD = _maxDrawdownCeiling > 0
            ? Math.Clamp(1.0 - (double)maxDrawdownPct / _maxDrawdownCeiling, 0, 1) : 0.5;
        double normWR = Math.Clamp((double)winRate, 0, 1);
        return (normS, normDD, normWR);
    }

    private void AddToParetoFront(double[] point)
    {
        _paretoFront.Add(point);
        _paretoFront = ExtractNonDominated(_paretoFront);
        if (_paretoFront.Count > MaxParetoFrontSize)
            _paretoFront = PruneFrontByCrowding(_paretoFront, MaxParetoFrontSize);
    }

    private static void UpdateParetoFrontWorking(List<double[]> front, double[] newPoint)
    {
        front.Add(newPoint);
        var nonDom = ExtractNonDominated(front);
        front.Clear();
        front.AddRange(nonDom);
    }
}
