using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Shared helper that determines whether an ML model's signal suppression can be safely lifted
/// by checking conformal breaker state, emergency retrain status, and Kelly fraction.
/// </summary>
internal static class MLSuppressionStateHelper
{
    /// <summary>
    /// Returns <c>true</c> if all suppression conditions have cleared for the given model:
    /// no active conformal breakers, no emergency retrain in progress, and no negative-EV Kelly fraction.
    /// </summary>
    internal static async Task<bool> CanLiftSuppressionAsync(
        DbContext db,
        MLModel model,
        CancellationToken ct,
        long? ignoreConformalBreakerId = null,
        IReadOnlyCollection<long>? ignoreConformalBreakerIds = null)
    {
        var ignoredBreakerIds = ignoreConformalBreakerIds ?? Array.Empty<long>();
        bool breakerStillActive = await db.Set<MLConformalBreakerLog>()
            .AnyAsync(b => b.MLModelId == model.Id
                        && b.IsActive
                        && !b.IsDeleted
                        && (!ignoreConformalBreakerId.HasValue || b.Id != ignoreConformalBreakerId.Value)
                        && !ignoredBreakerIds.Contains(b.Id), ct);
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
            .Where(l => l.MLModelId == model.Id && l.IsReliable && !l.IsDeleted)
            .OrderByDescending(l => l.ComputedAt)
            .Select(l => l.NegativeEV)
            .FirstOrDefaultAsync(ct);
        if (latestKellyNegative)
            return false;

        return true;
    }
}
