using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Workers;

internal static class MLModelSnapshotWriteHelper
{
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
