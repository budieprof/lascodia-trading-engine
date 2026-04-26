using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Inputs to a single chronic-trip alert upsert. Bundles every field the upserter writes
/// so callers don't have to thread eight primitives through every call.
/// </summary>
/// <remarks>
/// <see cref="Timeframe"/> is the <see cref="LascodiaTradingEngine.Domain.Enums.Timeframe"/>
/// enum from the Domain layer. The Application layer (which contains this interface)
/// already depends on Domain, so this is a routine cross-layer reference rather than a
/// leak — there's no value in re-defining a parallel enum or stringifying the timeframe
/// at the interface boundary.
/// </remarks>
/// <param name="DeduplicationKey">Stable key for this <c>(model, symbol, timeframe)</c>.
/// Typically <c>"ml-conformal-chronic-trip:{modelId}"</c>.</param>
/// <param name="Symbol">Symbol the model is registered against. Persisted for routing.</param>
/// <param name="Timeframe">Timeframe the model is registered against. Embedded in
/// the alert's <c>ConditionJson</c> for operator context.</param>
/// <param name="ModelId">Foreign-key id of the offending model.</param>
/// <param name="ConsecutiveTripStreak">Current trip-cycle streak that triggered the
/// chronic alert.</param>
/// <param name="ChronicTripThreshold">Operator-configured threshold; embedded in the
/// alert's <c>ConditionJson</c> so a paged operator can see the streak relative to the
/// rule that fired it.</param>
/// <param name="CooldownSeconds">Persisted on <see cref="Alert.CooldownSeconds"/>; used
/// by the alert dispatcher to suppress duplicate notifications while the chronic
/// condition persists.</param>
/// <param name="EvaluatedAtUtc">Cycle's reference timestamp. Embedded in the alert's
/// <c>ConditionJson</c> and used for any timestamp-relative comparisons.</param>
public readonly record struct ChronicTripAlertContext(
    string DeduplicationKey,
    string Symbol,
    Timeframe Timeframe,
    long ModelId,
    int ConsecutiveTripStreak,
    int ChronicTripThreshold,
    int CooldownSeconds,
    DateTime EvaluatedAtUtc);

/// <summary>
/// Upserts the durable chronic-trip <see cref="Alert"/> row for a model that has crossed
/// <c>MLConformalBreakerOptions.ChronicTripThreshold</c>. Extracted from
/// <c>MLConformalBreakerWorker</c> so the SQL upsert path is independently testable
/// (including under concurrent invocation against real Postgres) without the worker
/// having to leak private internals via <c>internal</c> modifiers.
/// </summary>
public interface IMLConformalChronicTripAlertUpserter
{
    /// <summary>
    /// Inserts a new active chronic-trip alert with the given dedup key, or refreshes the
    /// existing row's diagnostics in place. Idempotent under concurrent invocation: the
    /// partial unique index on <c>Alert.DeduplicationKey</c> serializes racing replicas,
    /// so exactly one row exists for a given dedup key after any number of concurrent
    /// callers complete.
    /// </summary>
    Task<Alert> UpsertAsync(
        DbContext db,
        ChronicTripAlertContext context,
        CancellationToken ct);
}
