using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LascodiaTradingEngine.Application.Workers;

public sealed class MLConformalPredictionLogReader : IMLConformalPredictionLogReader
{
    public async Task<IReadOnlyDictionary<long, List<MLModelPredictionLog>>> LoadRecentResolvedLogsByModelAsync(
        DbContext db,
        IReadOnlyCollection<long> modelIds,
        int maxLogs,
        CancellationToken ct)
    {
        if (modelIds.Count == 0)
            return new Dictionary<long, List<MLModelPredictionLog>>();

        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            return await LoadWithPostgresWindowAsync(db, modelIds, maxLogs, ct);

        return await LoadWithProviderFallbackAsync(db, modelIds, maxLogs, ct);
    }

    private static async Task<IReadOnlyDictionary<long, List<MLModelPredictionLog>>> LoadWithProviderFallbackAsync(
        DbContext db,
        IReadOnlyCollection<long> modelIds,
        int maxLogs,
        CancellationToken ct)
    {
        var result = new Dictionary<long, List<MLModelPredictionLog>>();
        foreach (var modelId in modelIds)
        {
            ct.ThrowIfCancellationRequested();
            result[modelId] = await db.Set<MLModelPredictionLog>()
                .AsNoTracking()
                .Where(l => l.MLModelId == modelId
                            && l.ActualDirection != null
                            && l.OutcomeRecordedAt != null
                            && !l.IsDeleted)
                .OrderByDescending(l => l.OutcomeRecordedAt)
                .ThenByDescending(l => l.Id)
                .Take(maxLogs)
                .ToListAsync(ct);
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<long, List<MLModelPredictionLog>>> LoadWithPostgresWindowAsync(
        DbContext db,
        IReadOnlyCollection<long> modelIds,
        int maxLogs,
        CancellationToken ct)
    {
        var entityType = db.Model.FindEntityType(typeof(MLModelPredictionLog))
            ?? throw new InvalidOperationException("MLModelPredictionLog entity mapping was not found.");
        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException("MLModelPredictionLog table mapping was not found.");
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);
        var table = schema is null
            ? QuoteIdentifier(tableName)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(tableName)}";
        var columns = entityType.GetProperties()
            .Select(p => p.GetColumnName(storeObject))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var innerColumns = string.Join(", ", columns.Select(c => $"l.{QuoteIdentifier(c!)}"));
        var outerColumns = string.Join(", ", columns.Select(c => $"ranked.{QuoteIdentifier(c!)}"));

        var parameters = new List<object>();
        var modelIdPlaceholders = new List<string>();
        int parameterIndex = 0;
        foreach (var modelId in modelIds)
        {
            var parameter = db.Database.GetDbConnection().CreateCommand().CreateParameter();
            parameter.ParameterName = $"modelId{parameterIndex++}";
            parameter.Value = modelId;
            parameters.Add(parameter);
            modelIdPlaceholders.Add($"@{parameter.ParameterName}");
        }

        var maxLogsParameter = db.Database.GetDbConnection().CreateCommand().CreateParameter();
        maxLogsParameter.ParameterName = "maxLogs";
        maxLogsParameter.Value = maxLogs;
        parameters.Add(maxLogsParameter);

        var sql = $"""
            SELECT {outerColumns}
            FROM (
                SELECT {innerColumns},
                       ROW_NUMBER() OVER (
                           PARTITION BY l."MLModelId"
                           ORDER BY l."OutcomeRecordedAt" DESC, l."Id" DESC
                       ) AS "__rn"
                FROM {table} AS l
                WHERE l."MLModelId" IN ({string.Join(", ", modelIdPlaceholders)})
                  AND l."ActualDirection" IS NOT NULL
                  AND l."OutcomeRecordedAt" IS NOT NULL
                  AND l."IsDeleted" = FALSE
            ) AS ranked
            WHERE ranked."__rn" <= @maxLogs
            ORDER BY ranked."MLModelId", ranked."OutcomeRecordedAt" DESC, ranked."Id" DESC
            """;

        var logs = await db.Set<MLModelPredictionLog>()
            .FromSqlRaw(sql, parameters.ToArray())
            .AsNoTracking()
            .ToListAsync(ct);

        return logs
            .GroupBy(l => l.MLModelId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
