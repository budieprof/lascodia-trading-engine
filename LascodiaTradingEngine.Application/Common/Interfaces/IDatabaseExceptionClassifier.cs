using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Classifies provider-specific <see cref="DbUpdateException"/> instances into stable,
/// provider-agnostic buckets so Application code can make branching decisions without
/// taking a hard dependency on <c>Npgsql</c> (or any other ADO.NET provider).
/// </summary>
public interface IDatabaseExceptionClassifier
{
    /// <summary>
    /// Returns <c>true</c> if the exception (or any wrapped inner exception) represents
    /// a unique-constraint / duplicate-key violation. On PostgreSQL this maps to
    /// <c>SqlState = "23505"</c>.
    /// </summary>
    bool IsUniqueConstraintViolation(DbUpdateException ex);
}
