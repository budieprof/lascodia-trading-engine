using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Workers;

internal static class MLSuppressionStateHelper
{
    internal static async Task<bool> CanLiftSuppressionAsync(
        DbContext db,
        MLModel model,
        CancellationToken ct,
        long? ignoreConformalBreakerId = null)
    {
        bool breakerStillActive = await db.Set<MLConformalBreakerLog>()
            .AnyAsync(b => b.MLModelId == model.Id
                        && b.IsActive
                        && !b.IsDeleted
                        && (!ignoreConformalBreakerId.HasValue || b.Id != ignoreConformalBreakerId.Value), ct);
        if (breakerStillActive)
            return false;

        bool emergencyRetrainActive = await db.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol == model.Symbol
                        && r.Timeframe == model.Timeframe
                        && r.IsEmergencyRetrain
                        && !r.IsDeleted
                        && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);
        if (emergencyRetrainActive)
            return false;

        bool latestKellyNegative = await db.Set<MLKellyFractionLog>()
            .Where(l => l.MLModelId == model.Id && !l.IsDeleted)
            .OrderByDescending(l => l.ComputedAt)
            .Select(l => l.NegativeEV)
            .FirstOrDefaultAsync(ct);
        if (latestKellyNegative)
            return false;

        return true;
    }
}
