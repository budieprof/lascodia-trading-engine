using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Shared helper for ML workers that need to load and deserialize a tracked <see cref="MLModel"/>
/// and its <see cref="ModelSnapshot"/> from the write DbContext.
/// </summary>
internal static class MLModelSnapshotWriteHelper
{
    /// <summary>
    /// Loads the <see cref="MLModel"/> by ID from the write context, reloads it to get the
    /// latest state, and deserializes the <see cref="ModelSnapshot"/> from <c>ModelBytes</c>.
    /// Returns <c>(null, null)</c> if the model is deleted, missing, or has no valid snapshot.
    /// </summary>
    internal static async Task<(MLModel? Model, ModelSnapshot? Snapshot)> LoadTrackedLatestSnapshotAsync(
        DbContext writeCtx,
        long      modelId,
        CancellationToken ct)
    {
        var writeModel = await writeCtx.Set<MLModel>()
            .FirstOrDefaultAsync(m => m.Id == modelId && !m.IsDeleted, ct);
        if (writeModel == null)
            return (null, null);

        await writeCtx.Entry(writeModel).ReloadAsync(ct);
        if (writeModel.IsDeleted || writeModel.ModelBytes is not { Length: > 0 })
            return (null, null);

        try
        {
            var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(writeModel.ModelBytes);
            return snapshot == null ? (null, null) : (writeModel, snapshot);
        }
        catch
        {
            return (writeModel, null);
        }
    }
}
