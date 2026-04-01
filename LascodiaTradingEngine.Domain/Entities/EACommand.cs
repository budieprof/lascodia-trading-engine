using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a command queued by the engine for execution by an EA instance on MT5.
/// The EA polls for pending commands, executes them on the broker, and acknowledges completion.
/// </summary>
/// <remarks>
/// Commands flow engine -> EA via two delivery paths:
/// 1. Push (primary): The <see cref="Workers.TcpBridgeWorker"/> pushes commands over the
///    persistent TCP connection into the DLL's command ring buffer.
/// 2. Pull (fallback): The EA polls GET /commands with its instance ID as an HTTP fallback.
/// In both cases the EA calls PUT /commands/{id}/ack to confirm execution. Retryable
/// statuses (TimedOut, Deferred) re-queue the command up to <see cref="MaxRetries"/> times.
/// </remarks>
public class EACommand : Entity<long>
{
    /// <summary>
    /// The EA instance this command is targeted at. Must match an active
    /// <see cref="EAInstance.InstanceId"/>.
    /// </summary>
    public string TargetInstanceId { get; set; } = string.Empty;

    /// <summary>The type of broker-side action to perform.</summary>
    public EACommandType CommandType { get; set; }

    /// <summary>
    /// The broker ticket number of the position or order to act on.
    /// Null for commands that do not target a specific ticket (e.g. RequestBackfill).
    /// </summary>
    public long? TargetTicket { get; set; }

    /// <summary>
    /// The instrument symbol relevant to this command (e.g. "EURUSD").
    /// Used by the EA to identify the correct chart/symbol context.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// JSON-serialised parameter bag for command-specific data.
    /// For ModifySLTP: {"stopLoss": 1.0850, "takeProfit": 1.0950}
    /// For RequestBackfill: {"timeframe": "H1", "bars": 500}
    /// </summary>
    public string? Parameters { get; set; }

    /// <summary>
    /// When <c>true</c>, the EA has confirmed that this command was executed
    /// (successfully or otherwise). No further delivery attempts are made.
    /// </summary>
    public bool Acknowledged { get; set; }

    /// <summary>UTC timestamp when the EA acknowledged this command. Null until acknowledged.</summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// Result or error message returned by the EA after executing the command.
    /// Null until acknowledged.
    /// </summary>
    public string? AckResult { get; set; }

    /// <summary>
    /// Number of times this command has been re-queued after a retryable ACK
    /// (TimedOut / Deferred).  Capped at <see cref="MaxRetries"/> to prevent
    /// infinite re-delivery loops.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>Maximum retry attempts before the command is permanently acknowledged.</summary>
    public const int MaxRetries = 3;

    // ── Shared retry/ack logic ──────────────────────────────────────────────

    /// <summary>
    /// Determines whether a status string represents a retryable outcome.
    /// Used by both the REST ACK endpoint and the TCP bridge ACK handler.
    /// </summary>
    public static bool IsRetryableStatus(string? status) =>
        status is not null
        && (status.Contains("TimedOut", StringComparison.OrdinalIgnoreCase)
         || status.Contains("Deferred", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Attempts to re-queue this command for retry. Returns true if re-queued,
    /// false if max retries are exhausted (caller should finalize).
    /// </summary>
    public bool TryRequeue(string? status, string? result)
    {
        if (RetryCount >= MaxRetries)
            return false;

        RetryCount++;
        AckResult = $"{status}: {result}";
        return true;
    }

    /// <summary>
    /// Marks this command as permanently acknowledged (successful, failed, or max retries exhausted).
    /// </summary>
    public void FinalizeAck(bool isRetryable, bool success, string? result)
    {
        Acknowledged   = true;
        AcknowledgedAt = DateTime.UtcNow;
        AckResult      = isRetryable
            ? $"Max retries exceeded ({RetryCount}/{MaxRetries}): {result}"
            : (success ? (result ?? "OK") : (result ?? "FAILED"));
    }

    /// <summary>
    /// Unified ACK processing used by both the REST endpoint and the TCP bridge handler.
    /// Returns <c>true</c> if the command was re-queued for retry (caller should save but not finalize),
    /// <c>false</c> if the command was finalized (caller should save).
    /// </summary>
    public bool ProcessAck(string? status, string? result)
    {
        bool isRetryable = IsRetryableStatus(status);

        if (isRetryable && TryRequeue(status, result))
            return true;

        bool success = !isRetryable && string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase);
        FinalizeAck(isRetryable, success, result);
        return false;
    }

    /// <summary>UTC timestamp when this command was created/queued.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
