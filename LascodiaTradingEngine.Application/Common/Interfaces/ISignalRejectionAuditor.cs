namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Canonical entry point for recording every signal rejection / suppression
/// across the strategy pipeline. Writes one <c>SignalRejectionAudit</c> row per
/// invocation, emits the <c>SignalRejectionsAudited</c> metric tagged by
/// <paramref name="stage"/> and <paramref name="reason"/>, and swallows audit-
/// write failures — the caller's rejection decision must always succeed even if
/// the audit write fails.
/// </summary>
/// <remarks>
/// <para>
/// Stages are a small, fixed vocabulary: <c>Prefetch</c>, <c>Regime</c>,
/// <c>News</c>, <c>Evaluator</c>, <c>MTF</c>, <c>Correlation</c>, <c>Hawkes</c>,
/// <c>MLScoring</c>, <c>Abstention</c>, <c>ConflictResolution</c>,
/// <c>Tier1</c>, <c>Tier2</c>, <c>MLModelStale</c>, <c>PaperRouting</c>.
/// Reasons are short machine-readable codes scoped to the stage.
/// </para>
/// <para>
/// Implementations are expected to be cheap (single insert, no joins). Callers
/// should prefer the fire-and-forget pattern but must still await — the
/// underlying DB context is scoped.
/// </para>
/// </remarks>
public interface ISignalRejectionAuditor
{
    /// <summary>
    /// Records a single rejection. Returns when the audit row is persisted or
    /// the write has failed (in which case the failure is logged and swallowed).
    /// </summary>
    /// <param name="stage">Pipeline stage short code (e.g. "Prefetch", "Tier1").</param>
    /// <param name="reason">Specific reason within the stage (e.g. "regime_prefetch_timeout").</param>
    /// <param name="symbol">Currency pair affected (required).</param>
    /// <param name="source">Worker / service name recording the rejection.</param>
    /// <param name="strategyId">Strategy ID, or 0 for tick-level rejections.</param>
    /// <param name="tradeSignalId">TradeSignal ID, or null for pre-creation rejections.</param>
    /// <param name="detail">Optional human-readable detail (max 2000 chars).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordAsync(
        string stage,
        string reason,
        string symbol,
        string source,
        long strategyId = 0,
        long? tradeSignalId = null,
        string? detail = null,
        CancellationToken ct = default);
}
