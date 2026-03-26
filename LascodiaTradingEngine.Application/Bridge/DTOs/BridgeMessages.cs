namespace LascodiaTradingEngine.Application.Bridge.DTOs;

/// <summary>
/// Newline-delimited JSON messages exchanged over the TCP bridge connection.
/// Each message is serialized as a single JSON object ending with '\n'.
/// </summary>

// ── Inbound (EA → Engine) ─────────────────────────────────────────────────

/// <summary>Auth handshake sent by the EA immediately after connecting.</summary>
public record BridgeAuthMessage(
    string Type,        // "auth" | "reauth"
    string Token);      // JWT Bearer token

/// <summary>Execution report pushed by the EA after a fill.</summary>
public record BridgeReportMessage(
    string   Type,             // "report"
    long     SignalId,
    long     EngineOrderId,
    long     Mt5OrderTicket,
    long     Mt5DealTicket,
    long     MagicNumber,
    ulong    RequestId,
    int      Status,           // ENUM_EXEC_STATUS as int
    double   RequestedPrice,
    double   FilledPrice,
    double   RequestedVolume,
    double   FilledVolume,
    double   RemainingVolume,
    double   SlippagePips,
    int      SlippagePoints,
    double   Commission,
    int      ExecutionLatencyMs,
    int      QueueDwellMs,
    string   FillPolicy,
    string   AccountMode,
    bool     ExcessiveSlippage,
    bool     SlippageRejected,
    int      BrokerRetcode,
    string   ErrorMessage,
    long     Timestamp);       // Unix seconds

/// <summary>Heartbeat / keep-alive sent by the EA.</summary>
public record BridgePingMessage(string Type);  // "ping"

// ── Outbound (Engine → EA) ─────────────────────────────────────────────────

/// <summary>
/// Trade signal pushed to the EA over the bridge.
/// Mirrors the REST GET /signals/pending-execution response shape,
/// with <see cref="EngineOrderId"/> included (signals must have an Order assigned).
/// </summary>
public record BridgeSignalMessage(
    string   Type,              // "signal"
    long     Id,
    string   Symbol,
    int      Direction,
    int      ExecutionType,
    double   EntryPrice,
    double   StopLoss,
    double   TakeProfit,
    double   LotSize,
    double   Confidence,
    long     StrategyId,
    string   StrategyName,
    long     ExpiresAt,         // Unix seconds
    bool     TrailingEnabled,
    int      TrailingType,
    int      TrailingPeriod,
    double   TrailingMultiplier,
    string   TrailingTimeframe,
    string   Notes,
    long     EngineOrderId,     // Assigned engine order ID (non-zero)
    int      PartialFillCount,
    long     CreatedAt);        // Unix seconds

/// <summary>Auth accepted response.</summary>
public record BridgeAuthOkMessage(string Type);   // "auth_ok"

/// <summary>Auth rejected response.</summary>
public record BridgeAuthFailMessage(
    string Type,    // "auth_fail"
    string Reason);

/// <summary>Heartbeat reply.</summary>
public record BridgePongMessage(string Type);     // "pong"
