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

        int covered = 0;
        int maxRun = 0;
        int currentRun = 0;

        foreach (var observation in observations)
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

        double empiricalCoverage = covered / (double)observations.Count;
        bool hasEnoughSamples = observations.Count >= options.MinLogs;
        bool trippedByRun = maxRun >= options.TriggerRunLength;
        double coverageFloor = Math.Max(0.0, options.TargetCoverage - options.CoverageTolerance);
        var (lowerBound, upperBound) = WilsonInterval(covered, observations.Count, options.WilsonConfidenceLevel);
        double pValue = BinomialLowerTailProbability(covered, observations.Count, options.TargetCoverage);
        bool trippedByCoverageFloor = options.UseWilsonCoverageFloor
            ? empiricalCoverage < coverageFloor && upperBound < coverageFloor
            : empiricalCoverage < coverageFloor && pValue <= options.StatisticalAlpha;
        var tripReason = (trippedByRun, trippedByCoverageFloor) switch
        {
            (true, true)   => MLConformalBreakerTripReason.Both,
            (true, false)  => MLConformalBreakerTripReason.ConsecutiveUncovered,
            (false, true)  => MLConformalBreakerTripReason.SustainedLowCoverage,
            _              => MLConformalBreakerTripReason.Unknown,
        };
        bool shouldTrip = hasEnoughSamples && tripReason != MLConformalBreakerTripReason.Unknown;

        return new ConformalCoverageEvaluation(
            observations.Count,
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
            GetLastOutcomeAt(observations));
    }

    private static bool IsFiniteProbability(double value)
        => double.IsFinite(value) && value >= 0.0 && value <= 1.0;

    private static DateTime? GetLastOutcomeAt(IReadOnlyCollection<ConformalObservation> observations)
    {
        var resolved = observations
            .Where(o => o.OutcomeRecordedAt.HasValue)
            .Select(o => o.OutcomeRecordedAt!.Value)
            .ToArray();

        return resolved.Length == 0 ? null : resolved.Max();
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
