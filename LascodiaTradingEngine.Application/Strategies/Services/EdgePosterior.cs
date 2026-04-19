namespace LascodiaTradingEngine.Application.Strategies.Services;

/// <summary>
/// Bayesian posterior over a strategy's true (out-of-sample) Sharpe ratio, given
/// an observed backtest Sharpe, number of trades, and number of candidate
/// strategies tested in the selection cycle. Reframes promotion from
/// <i>"observed Sharpe &gt; threshold"</i> to
/// <i>"P(true Sharpe &gt; threshold | data) &gt; confidence"</i>.
///
/// <para>
/// <b>Model:</b>
/// <list type="bullet">
/// <item><description>Prior: Sharpe ~ N(0, σ₀²) with σ₀ = 0.3 (weakly optimistic — most
///     strategies have no edge).</description></item>
/// <item><description>Likelihood: observed Sharpe from T trades is approximately
///     N(true Sharpe, 1/√T) (Lo, 2002 — variance of Sharpe ratio estimator).</description></item>
/// <item><description>Selection bias: effective trials count reduces the precision of the
///     observation by √(log(N)) (Bailey/López de Prado 2014, deflation).</description></item>
/// </list>
/// </para>
///
/// <para>
/// The posterior is conjugate-normal:
/// <c>μ_post = (σ_obs² · μ_prior + σ_prior² · obs) / (σ_prior² + σ_obs²)</c>
/// <c>σ_post² = 1 / (1/σ_prior² + 1/σ_obs²)</c>.
/// The posterior collapses toward the prior when the observation is imprecise
/// (few trades, many trials) and toward the observation when evidence is strong.
/// </para>
///
/// <para>
/// Wire as an additional promotion gate (<c>Promotion:MinEdgeProbability</c>,
/// default 0.70) OR query directly for decision-theoretic sizing — Kelly fraction
/// scales with posterior edge probability × expected magnitude.
/// </para>
/// </summary>
public interface IEdgePosterior
{
    EdgePosteriorResult Compute(EdgeObservation obs);
}

/// <summary>
/// Observed evidence about a strategy's edge. All four fields matter: the
/// observed Sharpe alone is meaningless without the trial count (deflation) and
/// trade count (observation precision).
/// </summary>
public sealed record EdgeObservation(
    double ObservedSharpe,
    int    NumberOfTrades,
    int    NumberOfTrials,
    double PriorSigma = 0.3);

/// <summary>
/// Posterior distribution over true Sharpe. <see cref="ProbabilityOfPositiveEdge"/>
/// is the gateable scalar; <see cref="PosteriorMean"/> and <see cref="PosteriorStdDev"/>
/// let downstream sizing (Kelly-style) consume the full distribution.
/// </summary>
public sealed record EdgePosteriorResult(
    double PosteriorMean,
    double PosteriorStdDev,
    double ProbabilityOfPositiveEdge,
    double ProbabilityOfSharpeAboveOne);

public sealed class EdgePosterior : IEdgePosterior
{
    public EdgePosteriorResult Compute(EdgeObservation obs)
    {
        if (obs.NumberOfTrades <= 1)
        {
            // No evidence — posterior equals prior
            return new EdgePosteriorResult(
                PosteriorMean: 0.0,
                PosteriorStdDev: obs.PriorSigma,
                ProbabilityOfPositiveEdge: 0.5,
                ProbabilityOfSharpeAboveOne: NormalSurvival(1.0, 0.0, obs.PriorSigma));
        }

        // Observation variance for Sharpe estimator (Lo 2002, Gaussian approximation)
        double sigmaObs = 1.0 / Math.Sqrt(obs.NumberOfTrades);

        // Selection-bias inflation: testing N strategies multiplies the effective
        // observation variance by log(N). Captures "you cherry-picked this winner
        // out of N tries — its Sharpe is less informative than a single a-priori
        // hypothesis would have been."
        int trials = Math.Max(2, obs.NumberOfTrials);
        double inflation = Math.Sqrt(Math.Log(trials));
        sigmaObs *= inflation;

        // Conjugate Gaussian posterior
        double priorVar = obs.PriorSigma * obs.PriorSigma;
        double obsVar   = sigmaObs * sigmaObs;
        double posteriorVar = 1.0 / (1.0 / priorVar + 1.0 / obsVar);
        double posteriorMean = posteriorVar * (0.0 / priorVar + obs.ObservedSharpe / obsVar);
        double posteriorStd = Math.Sqrt(posteriorVar);

        return new EdgePosteriorResult(
            PosteriorMean: posteriorMean,
            PosteriorStdDev: posteriorStd,
            ProbabilityOfPositiveEdge: NormalSurvival(0.0, posteriorMean, posteriorStd),
            ProbabilityOfSharpeAboveOne: NormalSurvival(1.0, posteriorMean, posteriorStd));
    }

    /// <summary>P(X &gt; x) for X ~ N(μ, σ) — uses the complementary error function.</summary>
    private static double NormalSurvival(double x, double mu, double sigma)
    {
        if (sigma < 1e-12) return x < mu ? 1.0 : 0.0;
        double z = (x - mu) / sigma;
        // Φ(z) via erf; P(X > x) = 1 - Φ(z) = 0.5 * erfc(z / √2)
        return 0.5 * Erfc(z / Math.Sqrt(2.0));
    }

    /// <summary>
    /// Complementary error function. Abramowitz &amp; Stegun 7.1.26 — good to ~1e-7,
    /// adequate for posterior-probability gating (we round to 2 decimals anyway).
    /// </summary>
    private static double Erfc(double x)
    {
        double sign = x >= 0 ? 1 : -1;
        double absX = Math.Abs(x);
        double t = 1.0 / (1.0 + 0.3275911 * absX);
        double y = 1.0 - ((((
                  1.061405429 * t
                - 1.453152027) * t
                + 1.421413741) * t
                - 0.284496736) * t
                + 0.254829592) * t * Math.Exp(-absX * absX);
        return 1.0 - sign * y;
    }
}
