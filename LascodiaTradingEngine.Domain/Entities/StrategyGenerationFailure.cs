using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

public class StrategyGenerationFailure : Entity<long>
{
    public string CandidateId { get; set; } = string.Empty;
    public string? CycleId { get; set; }
    public string CandidateHash { get; set; } = string.Empty;
    public StrategyType StrategyType { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public Timeframe Timeframe { get; set; } = Timeframe.H1;
    public string ParametersJson { get; set; } = string.Empty;
    public string FailureStage { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public string? DetailsJson { get; set; }
    public bool IsReported { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}
