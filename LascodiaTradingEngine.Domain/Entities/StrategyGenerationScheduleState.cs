using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

public class StrategyGenerationScheduleState : Entity<long>
{
    public string WorkerName { get; set; } = string.Empty;
    public DateTime? LastRunDateUtc { get; set; }
    public DateTime? CircuitBreakerUntilUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int RetriesThisWindow { get; set; }
    public DateTime? RetryWindowDateUtc { get; set; }
    public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}
