using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

public class StrategyGenerationCheckpoint : Entity<long>
{
    public string WorkerName { get; set; } = string.Empty;
    public string CycleId { get; set; } = string.Empty;
    public DateTime CycleDateUtc { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public bool UsedRestartSafeFallback { get; set; }
    public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}
