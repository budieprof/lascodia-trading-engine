using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Common.Utilities;

/// <summary>
/// Lightweight, provider-agnostic helpers for inspecting <see cref="DbUpdateException"/>
/// without taking a hard dependency on Npgsql in Application code.
/// </summary>
public static class DbExceptions
{
    /// <summary>
    /// True when <paramref name="ex"/> represents a unique-constraint violation, regardless
    /// of provider. Sniffs Postgres SQLSTATE 23505 reflectively and falls back to a string
    /// match for "unique constraint" / "UNIQUE constraint failed" so SQLite tests work too.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            var sqlStateProp = cur.GetType().GetProperty("SqlState");
            if (sqlStateProp?.GetValue(cur) is string sqlState && sqlState == "23505")
                return true;
            if (cur.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
                cur.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
