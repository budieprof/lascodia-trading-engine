using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

public class StrategyGenerationPendingArtifact : Entity<long>
{
    public long StrategyId { get; set; }
    public string CandidateId { get; set; } = string.Empty;
    public string? CycleId { get; set; }
    public string CandidatePayloadJson { get; set; } = string.Empty;
    public bool NeedsCreationAudit { get; set; }
    public bool NeedsCreatedEvent { get; set; }
    public bool NeedsAutoPromoteEvent { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public string? LastErrorMessage { get; set; }
    public bool IsDeleted { get; set; }
}
