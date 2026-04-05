using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Extension methods for safely querying entities when <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>
/// is needed. The global soft-delete query filter is bypassed, but the extension enforces that
/// callers explicitly opt in to including deleted rows, preventing accidental data leaks.
///
/// <b>Why this exists:</b> <c>IgnoreQueryFilters()</c> bypasses ALL global filters — not just
/// soft-delete. If tenant isolation or account scoping filters are added in the future,
/// raw <c>IgnoreQueryFilters()</c> calls would silently bypass them. This extension makes the
/// intent clear and provides a single place to re-apply non-soft-delete filters when they're added.
/// </summary>
public static class QueryFilterExtensions
{
    /// <summary>
    /// Returns an <see cref="IQueryable{T}"/> that includes soft-deleted entities by bypassing
    /// global query filters, then explicitly re-applying any non-soft-delete filters.
    /// Currently no additional filters exist, but when tenant/account isolation filters are
    /// added, they should be re-applied here.
    /// </summary>
    /// <remarks>
    /// Usage: <c>db.Set&lt;Strategy&gt;().IncludingSoftDeleted()</c> instead of
    /// <c>db.Set&lt;Strategy&gt;().IgnoreQueryFilters()</c>.
    /// </remarks>
    public static IQueryable<T> IncludingSoftDeleted<T>(this DbSet<T> dbSet) where T : class
    {
        // Step 1: Bypass all global filters (including soft-delete)
        var query = dbSet.IgnoreQueryFilters();

        // Step 2: Re-apply non-soft-delete filters here when they exist.
        // Example (future): query = query.Where(e => EF.Property<long>(e, "TenantId") == currentTenantId);

        return query;
    }
}
