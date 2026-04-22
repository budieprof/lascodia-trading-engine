using Microsoft.EntityFrameworkCore;
using Npgsql;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Infrastructure.Services;

/// <summary>
/// PostgreSQL-specific <see cref="IDatabaseExceptionClassifier"/>. Uses the typed
/// <see cref="PostgresException"/> directly rather than reflection so diagnostics surface
/// the provider's own <c>SqlState</c> at compile time.
/// </summary>
public sealed class PostgresDatabaseExceptionClassifier : IDatabaseExceptionClassifier
{
    private const string UniqueViolationSqlState = "23505";

    public bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg && pg.SqlState == UniqueViolationSqlState)
                return true;
        }
        return false;
    }
}
