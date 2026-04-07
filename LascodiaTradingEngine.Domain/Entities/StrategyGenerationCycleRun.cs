using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

public class StrategyGenerationCycleRun : Entity<long>
{
    public string WorkerName { get; set; } = string.Empty;
    public string CycleId { get; set; } = string.Empty;
    public string Status { get; set; } = "Running";
    public string? Fingerprint { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public double? DurationMs { get; set; }
    public int CandidatesCreated { get; set; }
    public int ReserveCandidatesCreated { get; set; }
    public int CandidatesScreened { get; set; }
    public int SymbolsProcessed { get; set; }
    public int SymbolsSkipped { get; set; }
    public int StrategiesPruned { get; set; }
    public int PortfolioFilterRemoved { get; set; }
    public string? FailureStage { get; set; }
    public string? FailureMessage { get; set; }
    public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}
