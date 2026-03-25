using System.Linq.Expressions;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Utilities;

/// <summary>
/// Provides EF-translatable query helpers for <see cref="EAInstance"/> symbol lookups.
/// <para>
/// <see cref="EAInstance.Symbols"/> stores a comma-delimited list (e.g. "EURUSD,GBPUSD").
/// A naive <c>Symbols.Contains(symbol)</c> produces false positives when one symbol is a
/// substring of another (e.g. "EUR" matching "EURUSD"). These helpers match exact entries only.
/// </para>
/// </summary>
public static class EAInstanceQueryExtensions
{
    /// <summary>
    /// Returns an EF-translatable expression that matches <paramref name="symbol"/> as
    /// an exact comma-delimited entry within <see cref="EAInstance.Symbols"/>.
    /// Covers all four positions: sole entry, first, last, and middle.
    /// </summary>
    public static Expression<Func<EAInstance, bool>> OwnsSymbol(string symbol)
    {
        string prefix = symbol + ",";
        string suffix = "," + symbol;
        string middle = "," + symbol + ",";

        return ea => ea.Symbols == symbol
                  || ea.Symbols.StartsWith(prefix)
                  || ea.Symbols.EndsWith(suffix)
                  || ea.Symbols.Contains(middle);
    }

    /// <summary>
    /// Filters to active, non-deleted EA instances that own <paramref name="symbol"/>.
    /// </summary>
    public static IQueryable<EAInstance> ActiveForSymbol(
        this IQueryable<EAInstance> query, string symbol)
    {
        string prefix = symbol + ",";
        string suffix = "," + symbol;
        string middle = "," + symbol + ",";

        return query.Where(ea => ea.Status == EAInstanceStatus.Active
                              && !ea.IsDeleted
                              && (ea.Symbols == symbol
                                  || ea.Symbols.StartsWith(prefix)
                                  || ea.Symbols.EndsWith(suffix)
                                  || ea.Symbols.Contains(middle)));
    }

    /// <summary>
    /// Filters to active, non-deleted EA instances that own <paramref name="symbol"/>
    /// and have sent a heartbeat within <paramref name="maxHeartbeatAge"/>.
    /// Prevents strategy evaluation on stale data from disconnected EAs.
    /// </summary>
    public static IQueryable<EAInstance> ActiveAndFreshForSymbol(
        this IQueryable<EAInstance> query, string symbol, TimeSpan maxHeartbeatAge)
    {
        var cutoff = DateTime.UtcNow - maxHeartbeatAge;
        string prefix = symbol + ",";
        string suffix = "," + symbol;
        string middle = "," + symbol + ",";

        return query.Where(ea => ea.Status == EAInstanceStatus.Active
                              && !ea.IsDeleted
                              && ea.LastHeartbeat >= cutoff
                              && (ea.Symbols == symbol
                                  || ea.Symbols.StartsWith(prefix)
                                  || ea.Symbols.EndsWith(suffix)
                                  || ea.Symbols.Contains(middle)));
    }
}
