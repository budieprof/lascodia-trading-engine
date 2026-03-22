using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Strategy-level A/B testing framework with controlled traffic splitting.
/// Routes a configurable percentage of signals to a challenger variant while
/// the rest go to the control (champion) variant.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Experiment definition:</b> operator creates an experiment with a split ratio
///         (e.g., 80/20 champion/challenger).</item>
///   <item><b>Traffic routing:</b> <c>StrategyWorker</c> calls <see cref="GetVariant"/> before
///         signal generation. The variant determines which strategy parameters are used.</item>
///   <item><b>Outcome tracking:</b> each signal is tagged with its experiment/variant.
///         After enough outcomes, <see cref="EvaluateExperiment"/> computes the champion vs
///         challenger performance delta with statistical significance.</item>
///   <item><b>Auto-promotion:</b> when the challenger wins with sufficient confidence,
///         <see cref="PromoteChallenger"/> swaps it to champion and ends the experiment.</item>
/// </list>
/// </remarks>
public interface IABTestingService
{
    /// <summary>Creates a new A/B experiment for a strategy.</summary>
    string CreateExperiment(long strategyId, string challengerDescription, double challengerTrafficRatio = 0.20);

    /// <summary>Returns "champion" or "challenger" for the given strategy/signal.</summary>
    string GetVariant(long strategyId, long signalId);

    /// <summary>Records the outcome of a signal that was part of an experiment.</summary>
    void RecordOutcome(string experimentId, string variant, bool profitable, decimal pnlPips);

    /// <summary>Evaluates the experiment and returns the results.</summary>
    ABTestResult? EvaluateExperiment(string experimentId);

    /// <summary>Promotes the challenger to champion and ends the experiment.</summary>
    void PromoteChallenger(string experimentId);

    /// <summary>Ends the experiment without promoting the challenger.</summary>
    void EndExperiment(string experimentId);
}

/// <summary>Results of an A/B experiment evaluation.</summary>
public sealed record ABTestResult(
    string ExperimentId,
    int    ChampionTrades,
    int    ChallengerTrades,
    double ChampionWinRate,
    double ChallengerWinRate,
    double ChampionAvgPnl,
    double ChallengerAvgPnl,
    double PValueWinRate,
    bool   ChallengerWins,
    bool   StatisticallySignificant);

[RegisterService(ServiceLifetime.Singleton)]
public sealed class ABTestingService : IABTestingService
{
    private const int    MinTradesPerVariant = 30;
    private const double SignificanceLevel   = 0.05;

    private readonly ConcurrentDictionary<string, Experiment> _experiments = new();
    private readonly ConcurrentDictionary<long, string>       _strategyExperiments = new();
    private readonly ILogger<ABTestingService>                _logger;

    public ABTestingService(ILogger<ABTestingService> logger)
    {
        _logger = logger;
    }

    public string CreateExperiment(long strategyId, string challengerDescription, double challengerTrafficRatio = 0.20)
    {
        string id = $"ab_{strategyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        var experiment = new Experiment
        {
            Id                     = id,
            StrategyId             = strategyId,
            ChallengerDescription  = challengerDescription,
            ChallengerTrafficRatio = Math.Clamp(challengerTrafficRatio, 0.05, 0.50),
            CreatedAt              = DateTime.UtcNow,
        };

        _experiments[id] = experiment;
        _strategyExperiments[strategyId] = id;

        _logger.LogInformation(
            "A/B experiment created: {Id} for strategy {Strategy} ({Ratio:P0} challenger traffic) — {Desc}",
            id, strategyId, challengerTrafficRatio, challengerDescription);

        return id;
    }

    public string GetVariant(long strategyId, long signalId)
    {
        if (!_strategyExperiments.TryGetValue(strategyId, out var experimentId))
            return "champion";

        if (!_experiments.TryGetValue(experimentId, out var experiment) || experiment.Ended)
            return "champion";

        // Deterministic assignment based on signalId for reproducibility
        double hash = ((ulong)signalId * 2654435761ul % uint.MaxValue) / (double)uint.MaxValue;
        return hash < experiment.ChallengerTrafficRatio ? "challenger" : "champion";
    }

    public void RecordOutcome(string experimentId, string variant, bool profitable, decimal pnlPips)
    {
        if (!_experiments.TryGetValue(experimentId, out var experiment))
            return;

        var outcomes = variant == "challenger" ? experiment.ChallengerOutcomes : experiment.ChampionOutcomes;
        outcomes.Add(new Outcome(profitable, (double)pnlPips));
    }

    public ABTestResult? EvaluateExperiment(string experimentId)
    {
        if (!_experiments.TryGetValue(experimentId, out var experiment))
            return null;

        var champ = experiment.ChampionOutcomes;
        var chal  = experiment.ChallengerOutcomes;

        if (champ.Count < MinTradesPerVariant || chal.Count < MinTradesPerVariant)
            return null; // not enough data yet

        double champWinRate = champ.Count(o => o.Profitable) / (double)champ.Count;
        double chalWinRate  = chal.Count(o => o.Profitable) / (double)chal.Count;
        double champAvgPnl  = champ.Average(o => o.PnlPips);
        double chalAvgPnl   = chal.Average(o => o.PnlPips);

        // Two-proportion z-test for win rates
        double pooledP = (champ.Count(o => o.Profitable) + chal.Count(o => o.Profitable))
                         / (double)(champ.Count + chal.Count);
        double se      = Math.Sqrt(pooledP * (1 - pooledP) * (1.0 / champ.Count + 1.0 / chal.Count));
        double zStat   = se > 1e-10 ? (chalWinRate - champWinRate) / se : 0;
        double pValue  = 2.0 * (1.0 - NormalCdf(Math.Abs(zStat))); // two-tailed

        bool chalWins     = chalWinRate > champWinRate && chalAvgPnl > champAvgPnl;
        bool significant  = pValue < SignificanceLevel;

        var result = new ABTestResult(
            experimentId, champ.Count, chal.Count,
            champWinRate, chalWinRate, champAvgPnl, chalAvgPnl,
            pValue, chalWins, significant);

        _logger.LogInformation(
            "A/B experiment {Id}: champ={CWR:P1}/{CAvg:F1}pips ({CN}), chal={XWR:P1}/{XAvg:F1}pips ({XN}), p={P:F4}, chalWins={W}, sig={S}",
            experimentId, champWinRate, champAvgPnl, champ.Count,
            chalWinRate, chalAvgPnl, chal.Count, pValue, chalWins, significant);

        return result;
    }

    public void PromoteChallenger(string experimentId)
    {
        if (!_experiments.TryGetValue(experimentId, out var experiment))
            return;

        experiment.Ended = true;
        _strategyExperiments.TryRemove(experiment.StrategyId, out _);

        _logger.LogWarning(
            "A/B experiment {Id}: challenger PROMOTED for strategy {Strategy} — {Desc}",
            experimentId, experiment.StrategyId, experiment.ChallengerDescription);
    }

    public void EndExperiment(string experimentId)
    {
        if (!_experiments.TryGetValue(experimentId, out var experiment))
            return;

        experiment.Ended = true;
        _strategyExperiments.TryRemove(experiment.StrategyId, out _);

        _logger.LogInformation(
            "A/B experiment {Id}: ended without promotion for strategy {Strategy}",
            experimentId, experiment.StrategyId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Standard normal CDF approximation (Abramowitz & Stegun 26.2.17).</summary>
    private static double NormalCdf(double x)
    {
        if (x < -8) return 0;
        if (x >  8) return 1;
        double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(x));
        double d = 0.3989422804014327; // 1/sqrt(2π)
        double p = d * Math.Exp(-0.5 * x * x) *
                   (t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429)))));
        return x >= 0 ? 1.0 - p : p;
    }

    private sealed record Outcome(bool Profitable, double PnlPips);

    private sealed class Experiment
    {
        public string   Id                     { get; init; } = "";
        public long     StrategyId             { get; init; }
        public string   ChallengerDescription  { get; init; } = "";
        public double   ChallengerTrafficRatio { get; init; }
        public DateTime CreatedAt              { get; init; }
        public bool     Ended                  { get; set; }

        public List<Outcome> ChampionOutcomes   { get; } = [];
        public List<Outcome> ChallengerOutcomes { get; } = [];
    }
}
