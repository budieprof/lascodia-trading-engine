using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

public class StrategyGenerationFeedbackState : Entity<long>
{
    public string StateKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime LastUpdatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}
