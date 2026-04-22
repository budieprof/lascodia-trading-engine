using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persisted terminal decision for a signal-level champion/challenger A/B test.
/// Captures the sample counts, P&amp;L metrics, SPRT statistic, and reason that drove
/// the production lifecycle action.
/// </summary>
public class MLSignalAbTestResult : Entity<long>
{
    public long ChampionModelId { get; set; }
    public long ChallengerModelId { get; set; }

    public string Symbol { get; set; } = string.Empty;
    public Timeframe Timeframe { get; set; } = Timeframe.H1;

    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; } = DateTime.UtcNow;

    public string Decision { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    public int ChampionTradeCount { get; set; }
    public int ChallengerTradeCount { get; set; }

    public decimal ChampionAvgPnl { get; set; }
    public decimal ChallengerAvgPnl { get; set; }
    public decimal ChampionSharpe { get; set; }
    public decimal ChallengerSharpe { get; set; }
    public decimal SprtLogLikelihoodRatio { get; set; }
    public decimal CovariateImbalanceScore { get; set; }

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
