using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records the Maximum Relevance Minimum Redundancy (MRMR) feature importance ranking
/// computed for a symbol/timeframe pair (Rec #41). MRMR scores balance mutual information
/// with the target against mutual information among selected features, producing a ranking
/// more robust to correlated features than simple permutation importance.
/// </summary>
public class MLMrmrFeatureRanking : Entity<long>
{
    public string    Symbol          { get; set; } = string.Empty;
    public Timeframe Timeframe       { get; set; } = Timeframe.H1;
    /// <summary>Feature name (matches MLFeatureHelper.FeatureNames).</summary>
    public string    FeatureName     { get; set; } = string.Empty;
    /// <summary>Zero-based rank in the MRMR ordering (0 = most important).</summary>
    public int       MrmrRank        { get; set; }
    /// <summary>Mutual information with the binary direction target.</summary>
    public double    MutualInfoWithTarget { get; set; }
    /// <summary>Average mutual information with already-selected features (redundancy).</summary>
    public double    RedundancyScore { get; set; }
    /// <summary>MRMR score = MutualInfoWithTarget − RedundancyScore.</summary>
    public double    MrmrScore       { get; set; }
    /// <summary>Number of candles used in the MI estimation.</summary>
    public int       SampleCount     { get; set; }
    public DateTime  ComputedAt      { get; set; } = DateTime.UtcNow;
    public bool      IsDeleted       { get; set; }
}
