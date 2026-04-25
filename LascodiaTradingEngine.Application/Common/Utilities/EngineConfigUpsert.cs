using System.Text;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Utilities;

public readonly record struct EngineConfigUpsertSpec(
    string Key,
    string Value,
    ConfigDataType DataType = ConfigDataType.String,
    string? Description = null,
    bool IsHotReloadable = true);

/// <summary>
/// Race-safe upsert helper for <c>EngineConfig</c>. Uses PostgreSQL's atomic
/// <c>INSERT ... ON CONFLICT ("Key") DO UPDATE</c> so concurrent writers from
/// different workers cannot trigger a <c>23505</c> duplicate-key exception.
///
/// The previous pattern — <c>ExecuteUpdate</c> to check-if-row-exists, then
/// <c>Add</c> + <c>SaveChanges</c> on zero rows updated — has a TOCTOU race:
/// two workers running the check concurrently can both see "no row", both
/// queue an INSERT, and the second commit fails with a unique-constraint
/// violation. That exception escapes out of the worker's processing loop
/// and causes the training run (or whatever caller) to be re-queued for
/// retry, consuming all 3 attempts and permanently failing.
///
/// All paths share the same conflict-resolution semantics:
/// <list type="bullet">
///   <item>Value, DataType, IsHotReloadable, LastUpdatedAt always overwrite.</item>
///   <item>Description preserves the existing value when the new value is null
///         (<c>COALESCE(new, existing)</c>) and overwrites when non-null.</item>
///   <item>IsDeleted is reset to false to resurrect soft-deleted rows so
///         downstream readers using the standard soft-delete filter see them.</item>
/// </list>
/// </summary>
public static class EngineConfigUpsert
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private const string ConflictUpdateClause =
        " ON CONFLICT (\"Key\") DO UPDATE SET " +
        "\"Value\" = EXCLUDED.\"Value\", " +
        "\"DataType\" = EXCLUDED.\"DataType\", " +
        "\"Description\" = COALESCE(EXCLUDED.\"Description\", \"EngineConfig\".\"Description\"), " +
        "\"IsHotReloadable\" = EXCLUDED.\"IsHotReloadable\", " +
        "\"LastUpdatedAt\" = EXCLUDED.\"LastUpdatedAt\", " +
        "\"IsDeleted\" = false";

    public static Task UpsertAsync(
        DbContext ctx,
        string key,
        string value,
        ConfigDataType dataType = ConfigDataType.String,
        string? description = null,
        bool isHotReloadable = true,
        CancellationToken ct = default)
        => BatchUpsertAsync(
            ctx,
            new[] { new EngineConfigUpsertSpec(key, value, dataType, description, isHotReloadable) },
            ct);

    /// <summary>
    /// Atomically upserts many <c>EngineConfig</c> rows in a single round-trip.
    /// On Postgres, emits one <c>INSERT ... VALUES (...), (...) ON CONFLICT DO UPDATE</c>;
    /// on other providers, batches tracked changes and saves once. Late duplicate keys
    /// in the input win.
    /// </summary>
    public static Task BatchUpsertAsync(
        DbContext ctx,
        IReadOnlyList<EngineConfigUpsertSpec> specs,
        CancellationToken ct = default)
    {
        if (specs.Count == 0)
            return Task.CompletedTask;

        var deduped = DedupeKeepingLast(specs);

        return string.Equals(ctx.Database.ProviderName, PostgresProvider, StringComparison.Ordinal)
            ? BatchUpsertPostgresAsync(ctx, deduped, ct)
            : BatchUpsertTrackedAsync(ctx, deduped, ct);
    }

    private static List<EngineConfigUpsertSpec> DedupeKeepingLast(IReadOnlyList<EngineConfigUpsertSpec> specs)
    {
        var lastIndexByKey = new Dictionary<string, int>(specs.Count);
        for (int i = 0; i < specs.Count; i++)
            lastIndexByKey[specs[i].Key] = i;

        var deduped = new List<EngineConfigUpsertSpec>(lastIndexByKey.Count);
        for (int i = 0; i < specs.Count; i++)
        {
            if (lastIndexByKey[specs[i].Key] == i)
                deduped.Add(specs[i]);
        }
        return deduped;
    }

    private static Task BatchUpsertPostgresAsync(
        DbContext ctx,
        IReadOnlyList<EngineConfigUpsertSpec> specs,
        CancellationToken ct)
    {
        var sql = new StringBuilder(
            "INSERT INTO \"EngineConfig\" " +
            "(\"Key\", \"Value\", \"DataType\", \"Description\", \"IsHotReloadable\", \"LastUpdatedAt\", \"OutboxId\", \"IsDeleted\") VALUES ");

        var sqlBuilder = new ParameterizedSqlBuilder();
        string nowPlaceholder = sqlBuilder.AddParameter(DateTime.UtcNow);

        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            if (i > 0) sql.Append(", ");
            sql.Append('(')
                .Append(sqlBuilder.AddParameter(spec.Key)).Append(", ")
                .Append(sqlBuilder.AddParameter(spec.Value)).Append(", ")
                .Append(sqlBuilder.AddParameter(spec.DataType.ToString())).Append(", ")
                .Append(sqlBuilder.AddParameter(spec.Description)).Append(", ")
                .Append(sqlBuilder.AddParameter(spec.IsHotReloadable)).Append(", ")
                .Append(nowPlaceholder).Append(", ")
                .Append("gen_random_uuid(), false)");
        }

        sql.Append(ConflictUpdateClause);

        return ctx.Database.ExecuteSqlRawAsync(sql.ToString(), sqlBuilder.Parameters, ct);
    }

    private static async Task BatchUpsertTrackedAsync(
        DbContext ctx,
        IReadOnlyList<EngineConfigUpsertSpec> specs,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var keys = specs.Select(spec => spec.Key).ToList();

        var existing = await ctx.Set<EngineConfig>()
            .IgnoreQueryFilters()
            .Where(config => keys.Contains(config.Key))
            .ToDictionaryAsync(config => config.Key, ct);

        foreach (var spec in specs)
        {
            if (existing.TryGetValue(spec.Key, out var entry))
            {
                entry.Value = spec.Value;
                entry.DataType = spec.DataType;
                entry.Description = spec.Description ?? entry.Description;
                entry.IsHotReloadable = spec.IsHotReloadable;
                entry.LastUpdatedAt = now;
                entry.IsDeleted = false;
            }
            else
            {
                ctx.Set<EngineConfig>().Add(new EngineConfig
                {
                    Key = spec.Key,
                    Value = spec.Value,
                    DataType = spec.DataType,
                    Description = spec.Description,
                    IsHotReloadable = spec.IsHotReloadable,
                    LastUpdatedAt = now,
                    IsDeleted = false
                });
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    private sealed class ParameterizedSqlBuilder
    {
        private readonly List<object?> _parameters = new();

        public IReadOnlyList<object?> Parameters => _parameters;

        public string AddParameter(object? value)
        {
            int index = _parameters.Count;
            _parameters.Add(value);
            return "{" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        }
    }
}
