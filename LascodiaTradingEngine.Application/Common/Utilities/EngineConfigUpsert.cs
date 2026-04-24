using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Utilities;

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
/// </summary>
public static class EngineConfigUpsert
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// Atomically upserts a single <c>EngineConfig</c> key/value via a single
    /// parameterised SQL statement. Safe to call concurrently from multiple
    /// workers on the same key without coordination.
    ///
    /// <para>
    /// <b>Soft-delete resurrection:</b> if the row exists with <c>IsDeleted=true</c>
    /// (e.g. archived by a cleanup worker), the <c>DO UPDATE</c> branch resets
    /// <c>IsDeleted</c> to <c>false</c> so the new value is visible to readers that
    /// apply the standard soft-delete filter. Without this, every subsequent upsert
    /// silently updated the <c>Value</c> but the row stayed invisible, causing
    /// write-then-read-nothing loops (observed in MLDegradationModeWorker
    /// "DetectedAt is missing or invalid" firing every cycle).
    /// </para>
    /// </summary>
    public static Task UpsertAsync(
        DbContext ctx,
        string key,
        string value,
        ConfigDataType dataType = ConfigDataType.String,
        string? description = null,
        bool isHotReloadable = true,
        CancellationToken ct = default)
    {
        if (!string.Equals(ctx.Database.ProviderName, PostgresProvider, StringComparison.Ordinal))
        {
            return UpsertTrackedAsync(ctx, key, value, dataType, description, isHotReloadable, ct);
        }

        var dataTypeName = dataType.ToString();
        var now = DateTime.UtcNow;
        return ctx.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""EngineConfig""
                (""Key"", ""Value"", ""DataType"", ""Description"", ""IsHotReloadable"", ""LastUpdatedAt"", ""OutboxId"", ""IsDeleted"")
            VALUES
                ({key}, {value}, {dataTypeName}, {description}, {isHotReloadable}, {now}, gen_random_uuid(), false)
            ON CONFLICT (""Key"") DO UPDATE SET
                ""Value"" = EXCLUDED.""Value"",
                ""LastUpdatedAt"" = EXCLUDED.""LastUpdatedAt"",
                ""IsDeleted"" = false",
            ct);
    }

    private static async Task UpsertTrackedAsync(
        DbContext ctx,
        string key,
        string value,
        ConfigDataType dataType,
        string? description,
        bool isHotReloadable,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var entry = await ctx.Set<EngineConfig>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry is null)
        {
            ctx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key = key,
                Value = value,
                DataType = dataType,
                Description = description,
                IsHotReloadable = isHotReloadable,
                LastUpdatedAt = now,
                IsDeleted = false
            });
        }
        else
        {
            entry.Value = value;
            entry.DataType = dataType;
            entry.Description = description ?? entry.Description;
            entry.IsHotReloadable = isHotReloadable;
            entry.LastUpdatedAt = now;
            entry.IsDeleted = false;
        }

        await ctx.SaveChangesAsync(ct);
    }
}
