using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Backtesting;

public interface IStrategyExecutionSnapshotBuilder
{
    Task<string?> BuildSnapshotJsonAsync(
        DbContext writeDb,
        long strategyId,
        string? parametersJsonOverride,
        CancellationToken ct);

    StrategyExecutionSnapshot? Deserialize(string? strategySnapshotJson);
}

internal sealed class StrategyExecutionSnapshotBuilder : IStrategyExecutionSnapshotBuilder
{
    public async Task<string?> BuildSnapshotJsonAsync(
        DbContext writeDb,
        long strategyId,
        string? parametersJsonOverride,
        CancellationToken ct)
    {
        DbSet<Strategy>? strategySet;
        try
        {
            strategySet = writeDb.Set<Strategy>();
        }
        catch
        {
            return null;
        }

        if (strategySet is null)
            return null;

        var strategy = await strategySet
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == strategyId && !candidate.IsDeleted, ct);

        if (strategy == null)
            return null;

        return JsonSerializer.Serialize(
            StrategyExecutionSnapshot.FromStrategy(strategy, parametersJsonOverride));
    }

    public StrategyExecutionSnapshot? Deserialize(string? strategySnapshotJson)
        => StrategyExecutionSnapshot.Deserialize(strategySnapshotJson);
}
