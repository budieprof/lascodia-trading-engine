using Microsoft.EntityFrameworkCore;
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
}
