using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

public sealed class MLConformalCoverageEvaluator : IMLConformalCoverageEvaluator
{
    public ConformalCoverageEvaluation Evaluate(
        IReadOnlyCollection<ConformalObservation> observations,
        ConformalCoverageEvaluationOptions options)
    {
        if (!IsFiniteProbability(options.TargetCoverage)
            || !IsFiniteProbability(options.CoverageTolerance)
            || !IsFiniteProbability(options.WilsonConfidenceLevel)
            || !IsFiniteProbability(options.StatisticalAlpha)
            || options.MinLogs <= 0
            || options.TriggerRunLength <= 0)
        {
            return ConformalCoverageEvaluation.Empty(observations.Count);
        }

        if (observations.Count == 0)
            return ConformalCoverageEvaluation.Empty(0);

        // ── Per-regime decomposition (diagnostic-only) ──
        // Tag-aligned breakdown of empirical coverage by regime. Doesn't affect trip
        // semantics — the breaker still trips on the global signal — but the worst-regime
        // information enriches trip alerts so operators can immediately see "trending
        // coverage is healthy, ranging coverage is failing."
        var regimeBreakdown = ComputeRegimeBreakdown(observations);

        // Materialize ordered list once so we can iterate twice (covered count + run-length
        // detector + time-decay weighted mean) without retraversing the collection's
        // (potentially lazy) source.
        var ordered = observations.ToArray();

        // ── Plain unweighted coverage + consecutive-uncovered streak ──
        int covered = 0;
        int maxRun = 0;
        int currentRun = 0;
        foreach (var observation in ordered)
        {
            if (observation.WasCovered)
            {
                covered++;
                currentRun = 0;
            }
            else
            {
                currentRun++;
                if (currentRun > maxRun) maxRun = currentRun;
            }
        }

        double empiricalCoverage = covered / (double)ordered.Length;
        bool hasEnoughSamples = ordered.Length >= options.MinLogs;
        bool trippedByRun = maxRun >= options.TriggerRunLength;
        double coverageFloor = Math.Max(0.0, options.TargetCoverage - options.CoverageTolerance);
        var (lowerBound, upperBound) = WilsonInterval(covered, ordered.Length, options.WilsonConfidenceLevel);
        double pValue = BinomialLowerTailProbability(covered, ordered.Length, options.TargetCoverage);

        // ── Time-decay weighted coverage ──
        // Each observation gets weight exp(-ln(2) * age_days / halfLife). The weighted
        // empirical coverage is the weighted mean of the indicator, which more truthfully
        // reflects "how is this model doing right now" when a model just recovered from
        // miscalibration. Falls back to unweighted when half-life is zero.
        double weightedCoverage = options.TimeDecayHalfLifeDays > 0
            ? ComputeTimeDecayWeightedCoverage(ordered, options.TimeDecayHalfLifeDays, options.NowUtc)
            : empiricalCoverage;

        // ── Bootstrap stderr for K-sigma trend gating ──
        // Resamples observations with replacement, computes empirical coverage of each
        // resample, returns standard deviation. Deterministic seed via FNV-1a over
        // (modelId, sample count, first/last outcome timestamps) so two replicas observing
        // identical data compute identical stderr.
        double stderr = 0.0;
        int resamplesUsed = 0;
        if (options.BootstrapResamples > 0 && ordered.Length >= options.MinLogs)
        {
            (stderr, resamplesUsed) = ComputeBootstrapStderr(
                ordered, options.BootstrapResamples, options.ModelId);
        }

        // ── K-sigma gate on the sustained-low-coverage trip ──
        // Vanilla floor check: empirical < floor.
        // Wilson refinement: upper-CI < floor (already conservative).
        // K-sigma refinement: empirical < floor - K * stderr (rejects noisy almost-floor
        // values; requires stderr-bounded statistical evidence). Stacks with the Wilson
        // or p-value bound when configured.
        bool floorBreached = empiricalCoverage < coverageFloor;
        bool wilsonBreached = options.UseWilsonCoverageFloor && upperBound < coverageFloor;
        bool pValueBreached = !options.UseWilsonCoverageFloor && pValue <= options.StatisticalAlpha;
        bool kSigmaBreached = options.RegressionGuardK > 0 && stderr > 0
            ? empiricalCoverage < coverageFloor - options.RegressionGuardK * stderr
            : true; // If K-sigma is disabled, don't add an extra gate.

        bool trippedByCoverageFloor = floorBreached
            && (wilsonBreached || pValueBreached)
            && kSigmaBreached;

        var tripReason = (trippedByRun, trippedByCoverageFloor) switch
        {
            (true, true)   => MLConformalBreakerTripReason.Both,
            (true, false)  => MLConformalBreakerTripReason.ConsecutiveUncovered,
            (false, true)  => MLConformalBreakerTripReason.SustainedLowCoverage,
            _              => MLConformalBreakerTripReason.Unknown,
        };
        bool shouldTrip = hasEnoughSamples && tripReason != MLConformalBreakerTripReason.Unknown;

        return new ConformalCoverageEvaluation(
            ordered.Length,
            covered,
            empiricalCoverage,
            maxRun,
            shouldTrip,
            hasEnoughSamples,
            hasEnoughSamples && trippedByCoverageFloor,
            tripReason,
            lowerBound,
            upperBound,
            pValue,
            GetLastOutcomeAt(ordered),
            weightedCoverage,
            stderr,
            resamplesUsed,
            regimeBreakdown);
    }

    private static IReadOnlyDictionary<global::LascodiaTradingEngine.Domain.Enums.MarketRegime, RegimeCoverageBreakdown> ComputeRegimeBreakdown(
        IReadOnlyCollection<ConformalObservation> observations)
    {
        // Single pass — only allocate the dictionary if at least one observation carries a
        // regime tag, so the per-regime decomposition path costs nothing for callers that
        // don't enable it.
        Dictionary<global::LascodiaTradingEngine.Domain.Enums.MarketRegime, (int sample, int covered)>? counts = null;
        foreach (var obs in observations)
        {
            if (!obs.Regime.HasValue) continue;
            counts ??= new Dictionary<global::LascodiaTradingEngine.Domain.Enums.MarketRegime, (int, int)>();
            counts.TryGetValue(obs.Regime.Value, out var cur);
            counts[obs.Regime.Value] = (cur.sample + 1, cur.covered + (obs.WasCovered ? 1 : 0));
        }

        if (counts is null)
            return new Dictionary<global::LascodiaTradingEngine.Domain.Enums.MarketRegime, RegimeCoverageBreakdown>();

        var result = new Dictionary<global::LascodiaTradingEngine.Domain.Enums.MarketRegime, RegimeCoverageBreakdown>(counts.Count);
        foreach (var (regime, (sample, coveredCount)) in counts)
            result[regime] = new RegimeCoverageBreakdown(sample, coveredCount, coveredCount / (double)sample);
        return result;
    }

    private static double ComputeTimeDecayWeightedCoverage(
        ConformalObservation[] observations,
        int halfLifeDays,
        DateTime nowUtc)
    {
        double halfLife = Math.Max(1, halfLifeDays);
        double sumWeights = 0.0;
        double sumCovered = 0.0;
        foreach (var obs in observations)
        {
            DateTime when = obs.OutcomeRecordedAt ?? nowUtc;
            double ageDays = Math.Max(0.0, (nowUtc - when).TotalDays);
            double weight = Math.Pow(0.5, ageDays / halfLife);
            sumWeights += weight;
            if (obs.WasCovered) sumCovered += weight;
        }
        if (sumWeights <= 0) return 0.0;
        return Math.Clamp(sumCovered / sumWeights, 0.0, 1.0);
    }

    private static (double Stderr, int ResamplesUsed) ComputeBootstrapStderr(
        ConformalObservation[] observations,
        int resamples,
        long modelId)
    {
        // Deterministic seed via FNV-1a over (modelId, sampleCount, firstOutcomeTicks,
        // lastOutcomeTicks). Two replicas observing identical observations compute
        // identical stderr; the stderr is reproducible across cycles for the same input.
        int n = observations.Length;
        DateTime first = observations[0].OutcomeRecordedAt ?? DateTime.MinValue;
        DateTime last = observations[^1].OutcomeRecordedAt ?? DateTime.MaxValue;
        long firstTicks = first.Ticks;
        long lastTicks = last.Ticks;
        int seed = unchecked((int)Fnv1a(modelId, n, firstTicks, lastTicks));

        var rng = new Random(seed);
        double sum = 0.0;
        double sumSquares = 0.0;
        for (int r = 0; r < resamples; r++)
        {
            int coveredInResample = 0;
            for (int s = 0; s < n; s++)
            {
                int pick = rng.Next(n);
                if (observations[pick].WasCovered) coveredInResample++;
            }
            double resampleCoverage = coveredInResample / (double)n;
            sum += resampleCoverage;
            sumSquares += resampleCoverage * resampleCoverage;
        }
        double mean = sum / resamples;
        double variance = Math.Max(0.0, sumSquares / resamples - mean * mean);
        return (Math.Sqrt(variance), resamples);
    }

    /// <summary>
    /// FNV-1a 64-bit hash over the seed inputs. C# defaults to randomized
    /// <c>HashCode.Combine</c> across processes, which would make the bootstrap stderr
    /// non-reproducible across replicas; this fixed-mix variant guarantees the same
    /// output for the same inputs everywhere.
    /// </summary>
    private static ulong Fnv1a(long modelId, int sampleCount, long firstTicks, long lastTicks)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong h = offset;
        h = (h ^ (ulong)modelId) * prime;
        h = (h ^ (uint)sampleCount) * prime;
        h = (h ^ (ulong)firstTicks) * prime;
        h = (h ^ (ulong)lastTicks) * prime;
        return h;
    }

    private static bool IsFiniteProbability(double value)
        => double.IsFinite(value) && value >= 0.0 && value <= 1.0;

    private static DateTime? GetLastOutcomeAt(IReadOnlyList<ConformalObservation> observations)
    {
        DateTime? last = null;
        foreach (var obs in observations)
        {
            if (obs.OutcomeRecordedAt.HasValue
                && (last is null || obs.OutcomeRecordedAt.Value > last.Value))
                last = obs.OutcomeRecordedAt;
        }
        return last;
    }

    private static (double Lower, double Upper) WilsonInterval(int successes, int n, double confidenceLevel)
    {
        if (n <= 0) return (0.0, 1.0);

        double z = InverseStandardNormalCdf(0.5 + confidenceLevel / 2.0);
        double phat = successes / (double)n;
        double z2 = z * z;
        double denom = 1.0 + z2 / n;
        double centre = phat + z2 / (2.0 * n);
        double margin = z * Math.Sqrt((phat * (1.0 - phat) + z2 / (4.0 * n)) / n);
        return (
            Math.Clamp((centre - margin) / denom, 0.0, 1.0),
            Math.Clamp((centre + margin) / denom, 0.0, 1.0));
    }

    private static double InverseStandardNormalCdf(double p)
    {
        p = Math.Clamp(p, 1e-15, 1.0 - 1e-15);

        // Peter J. Acklam's rational approximation, accurate enough for confidence
        // interval construction while avoiding a heavy statistics package dependency.
        double[] a =
        [
            -3.969683028665376e+01,
             2.209460984245205e+02,
            -2.759285104469687e+02,
             1.383577518672690e+02,
            -3.066479806614716e+01,
             2.506628277459239e+00
        ];
        double[] b =
        [
            -5.447609879822406e+01,
             1.615858368580409e+02,
            -1.556989798598866e+02,
             6.680131188771972e+01,
            -1.328068155288572e+01
        ];
        double[] c =
        [
            -7.784894002430293e-03,
            -3.223964580411365e-01,
            -2.400758277161838e+00,
            -2.549732539343734e+00,
             4.374664141464968e+00,
             2.938163982698783e+00
        ];
        double[] d =
        [
             7.784695709041462e-03,
             3.224671290700398e-01,
             2.445134137142996e+00,
             3.754408661907416e+00
        ];

        const double plow = 0.02425;
        const double phigh = 1.0 - plow;

        if (p < plow)
        {
            double q = Math.Sqrt(-2.0 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
        }

        if (p > phigh)
        {
            double q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
        }

        double r = p - 0.5;
        double s = r * r;
        return (((((a[0] * s + a[1]) * s + a[2]) * s + a[3]) * s + a[4]) * s + a[5]) * r
            / (((((b[0] * s + b[1]) * s + b[2]) * s + b[3]) * s + b[4]) * s + 1.0);
    }

    private static double BinomialLowerTailProbability(int successes, int n, double p)
    {
        if (n <= 0) return 1.0;
        p = Math.Clamp(p, 1e-12, 1.0 - 1e-12);
        double logQ = Math.Log(1.0 - p);
        double probability = Math.Exp(n * logQ);
        double cumulative = probability;
        for (int k = 0; k < successes; k++)
        {
            probability *= ((n - k) / (double)(k + 1)) * (p / (1.0 - p));
            cumulative += probability;
        }
        return Math.Clamp(cumulative, 0.0, 1.0);
    }
}
