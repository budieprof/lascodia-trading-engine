using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a command queued by the engine for execution by an EA instance on MT5.
/// The EA polls for pending commands, executes them on the broker, and acknowledges completion.
/// </summary>
/// <remarks>
/// Commands flow engine -> EA (pull model). The EA calls GET /commands with its instance ID
/// to retrieve unacknowledged commands, executes them via MQL5, and calls PUT /commands/{id}/ack
/// to confirm execution. The engine never pushes commands directly.
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

    /// <summary>UTC timestamp when this command was created/queued.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
