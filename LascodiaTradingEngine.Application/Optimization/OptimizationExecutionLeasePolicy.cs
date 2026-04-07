using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationExecutionLeasePolicy
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    internal static void StampHeartbeat(OptimizationRun run)
        => OptimizationRunClaimer.StampHeartbeat(run, LeaseDuration);

    internal static async Task HeartbeatRunAsync(
        OptimizationRun run,
        IWriteApplicationDbContext writeCtx,
        CancellationToken ct)
    {
        StampHeartbeat(run);
        await writeCtx.SaveChangesAsync(ct);
    }

    internal static TimeSpan GetHeartbeatInterval()
    {
        long quarterLeaseTicks = LeaseDuration.Ticks / 4;
        long minIntervalTicks = TimeSpan.FromMinutes(1).Ticks;
        long maxIntervalTicks = TimeSpan.FromMinutes(3).Ticks;
        long boundedTicks = Math.Max(minIntervalTicks, Math.Min(maxIntervalTicks, quarterLeaseTicks));
        return TimeSpan.FromTicks(boundedTicks);
    }
}
