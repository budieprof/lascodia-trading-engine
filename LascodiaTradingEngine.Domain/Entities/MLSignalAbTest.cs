using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// First-class lifecycle record for an active or completed signal-level champion/challenger A/B test.
/// </summary>
public class MLSignalAbTest : Entity<long>
{
    public long ChampionModelId { get; set; }
    public long ChallengerModelId { get; set; }

    public string Symbol { get; set; } = string.Empty;
    public Timeframe Timeframe { get; set; } = Timeframe.H1;

    public MLSignalAbTestStatus Status { get; set; } = MLSignalAbTestStatus.Active;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
