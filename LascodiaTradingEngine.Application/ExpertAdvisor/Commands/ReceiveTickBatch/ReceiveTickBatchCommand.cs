using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveTickBatch;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Ingests a batch of real-time tick data streamed from a MetaTrader 5 Expert Advisor instance.
///
/// This is the primary market-data entry point for the engine when EA mode is active (built-in
/// broker adapters disabled). Each EA instance sends tick batches at a high frequency (typically
/// every 100–500 ms per symbol), making this one of the most latency-sensitive endpoints.
///
/// Processing pipeline:
///   1. Ownership guard — verifies the calling EA owns the declared InstanceId.
///   2. Heartbeat refresh — updates <see cref="EAInstance.LastHeartbeat"/>; reactivates
///      Disconnected instances (receiving ticks is proof of connectivity).
///   3. Stale/future tick filtering — drops ticks older than 60 s or with future timestamps
///      (clock skew &gt; 5 s) to prevent stale or invalid data from propagating downstream.
///   4. Live price cache update — writes the latest bid/ask per symbol into
///      <see cref="ILivePriceCache"/> for immediate consumption by workers and queries.
///   5. Event fan-out — publishes one <see cref="PriceUpdatedIntegrationEvent"/> per unique
///      symbol (latest tick wins) so downstream consumers (StrategyWorker, RiskMonitorWorker,
///      TrailingStopWorker, etc.) react to the freshest price only.
///
/// Validation constraints (see <see cref="ReceiveTickBatchCommandValidator"/>):
///   • InstanceId must be non-empty.
///   • Batch size capped at 5 000 ticks to bound per-request processing time.
///   • Each tick must have a valid symbol, positive bid/ask, ask ≥ bid, and a non-default timestamp.
///
/// Returns response code "00" on success, with a message indicating how many stale/future
/// ticks were silently dropped (if any).
/// </summary>
public class ReceiveTickBatchCommand : IRequest<ResponseData<string>>
{
    /// <summary>Unique identifier of the EA instance sending this batch (must match a registered, non-deleted instance).</summary>
    public required string InstanceId { get; set; }

    /// <summary>Ordered list of ticks captured by the EA since the last batch. May contain multiple symbols.</summary>
    public List<TickItem> Ticks { get; set; } = new();
}

/// <summary>
/// A single market tick captured by the EA from MetaTrader 5.
/// Represents a momentary bid/ask snapshot for one symbol at a specific point in time.
/// </summary>
public class TickItem
{
    /// <summary>Instrument symbol as reported by MT5 (e.g. "EURUSD", "GBPJPY"). Normalised to upper-case during processing.</summary>
    public required string Symbol { get; set; }

    /// <summary>Best available sell price (the price at which the broker will buy from the trader).</summary>
    public decimal Bid { get; set; }

    /// <summary>Best available buy price (the price at which the broker will sell to the trader). Must be ≥ Bid.</summary>
    public decimal Ask { get; set; }

    /// <summary>UTC timestamp when the tick was captured by the EA. Used for staleness checks (max age 60 s, max future drift 5 s).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Bid-ask spread in broker points. Sent by the EA for spread monitoring and analytics.</summary>
    public int? SpreadPoints { get; set; }

    /// <summary>Tick volume at the time of capture. Sent by the EA for volume-based analytics.</summary>
    public long? TickVolume { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates the tick batch before it reaches the handler. Runs automatically via the
/// MediatR <see cref="ValidationBehaviour{TRequest,TResponse}"/> pipeline.
/// Rejects the entire batch if any structural rule is violated (individual stale ticks
/// are filtered in the handler, not here, to avoid rejecting a whole batch for one bad tick).
/// </summary>
public class ReceiveTickBatchCommandValidator : AbstractValidator<ReceiveTickBatchCommand>
{
    public ReceiveTickBatchCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Ticks)
            .NotEmpty().WithMessage("Ticks list cannot be empty")
            // Upper bound prevents memory/CPU spikes from abnormally large payloads
            .Must(t => t.Count <= 5000).WithMessage("Tick batch cannot exceed 5000 items");

        RuleForEach(x => x.Ticks).ChildRules(tick =>
        {
            tick.RuleFor(t => t.Symbol).NotEmpty().WithMessage("Tick symbol cannot be empty");
            tick.RuleFor(t => t.Bid).GreaterThan(0).WithMessage("Bid must be greater than zero");
            tick.RuleFor(t => t.Ask).GreaterThan(0).WithMessage("Ask must be greater than zero");
            // Inverted spread (ask < bid) indicates corrupt data or a misconfigured symbol
            tick.RuleFor(t => t.Ask).GreaterThanOrEqualTo(t => t.Bid).WithMessage("Ask must be >= Bid");
            tick.RuleFor(t => t.Timestamp).NotEmpty().WithMessage("Tick timestamp cannot be empty");
        });
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Processes an incoming tick batch from a registered EA instance.
///
/// Execution flow:
///   1. Verify caller ownership via <see cref="IEAOwnershipGuard"/> (JWT-scoped instance check).
///   2. Touch <see cref="EAInstance.LastHeartbeat"/> — this doubles as an implicit heartbeat so the
///      EA doesn't need a separate heartbeat call while actively streaming ticks.
///      Disconnected instances are automatically reactivated here.
///   3. Iterate every tick in the batch:
///      a. Drop ticks older than 60 s (stale) or with future timestamps beyond 5 s (clock skew).
///      b. Write surviving ticks into <see cref="ILivePriceCache"/> — an in-memory (or DB-backed)
///         store that workers and queries read for the latest bid/ask.
///      c. Track the most recent tick per symbol for event publishing.
///   4. Publish one <see cref="PriceUpdatedIntegrationEvent"/> per symbol so downstream consumers
///      (strategy evaluation, trailing stop adjustment, risk monitoring, etc.) react to the
///      freshest price without processing every individual tick.
///   5. Return success with a count of dropped ticks (if any) for EA-side telemetry.
///
/// Performance notes:
///   • The handler does NOT persist individual ticks to the database — only the live cache is
///     updated. Historical candle storage is handled separately by <c>ReceiveCandleCommand</c>.
///   • The batch cap (5 000 ticks) and staleness filter keep per-request work bounded.
///   • SaveChangesAsync is called once for the heartbeat update, NOT per tick.
/// </summary>
public class ReceiveTickBatchCommandHandler : IRequestHandler<ReceiveTickBatchCommand, ResponseData<string>>
{
    private readonly ILivePriceCache _cache;
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;
    private readonly IEAOwnershipGuard _ownershipGuard;
    private readonly TradingMetrics _metrics;

    public ReceiveTickBatchCommandHandler(
        ILivePriceCache cache,
        IWriteApplicationDbContext context,
        IIntegrationEventService eventBus,
        IEAOwnershipGuard ownershipGuard,
        TradingMetrics metrics)
    {
        _cache = cache;
        _context = context;
        _eventBus = eventBus;
        _ownershipGuard = ownershipGuard;
        _metrics = metrics;
    }

    public async Task<ResponseData<string>> Handle(ReceiveTickBatchCommand request, CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // ── Step 1: Ownership check ──────────────────────────────────────────
        // Ensures the authenticated caller (JWT subject) actually owns the EA instance
        // they claim to be sending data for. Prevents cross-instance spoofing.
        if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
            return ResponseData<string>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        // ── Step 2: Implicit heartbeat ───────────────────────────────────────
        // Refresh EA heartbeat — receiving ticks is proof the EA is alive.
        // Also reactivate Disconnected instances since sending ticks proves connectivity.
        // This avoids the need for a separate heartbeat call while actively streaming.
        var eaInstance = await _context.GetDbContext()
            .Set<EAInstance>()
            .FirstOrDefaultAsync(ea => ea.InstanceId == request.InstanceId
                                    && !ea.IsDeleted, cancellationToken);

        if (eaInstance is not null)
        {
            eaInstance.LastHeartbeat = DateTime.UtcNow;
            // Auto-reactivate: if the instance was marked Disconnected by the health monitor
            // but is now sending ticks again, it's clearly back online.
            if (eaInstance.Status == EAInstanceStatus.Disconnected)
                eaInstance.Status = EAInstanceStatus.Active;

            // Always persist the heartbeat update immediately so it survives even when
            // all ticks are stale (no SaveAndPublish calls below to piggyback on).
            await _context.GetDbContext().SaveChangesAsync(cancellationToken);
        }

        // ── Step 3: Filter and cache ticks ───────────────────────────────────
        // Track the latest tick per symbol so we publish exactly one event per symbol (not per tick).
        // This deduplication is critical — a batch of 1000 EURUSD ticks should trigger one event,
        // not 1000, to avoid overwhelming downstream consumers.
        var latestBySymbol = new Dictionary<string, TickItem>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        const int maxTickAgeSeconds = 60; // Ticks older than this are considered stale
        int staleDropped = 0;

        foreach (var tick in request.Ticks)
        {
            // Reject ticks older than 60 seconds to prevent stale data from propagating.
            // This can happen if the EA buffers ticks during a network interruption and
            // sends them all at once upon reconnection.
            if ((now - tick.Timestamp).TotalSeconds > maxTickAgeSeconds)
            {
                staleDropped++;
                continue;
            }

            // Reject ticks with future timestamps (clock skew > 5 seconds).
            // MT5 server time and engine server time may drift; 5 s tolerance accommodates
            // minor NTP discrepancies without accepting clearly bogus timestamps.
            if (tick.Timestamp > now.AddSeconds(5))
            {
                staleDropped++;
                continue;
            }

            // Normalise symbol to upper-case for consistent cache keys and event matching
            var symbol = tick.Symbol.ToUpperInvariant();

            // Write to live price cache — this is what workers and queries read for current prices.
            // The cache is updated for every valid tick (not just the latest per symbol) so that
            // any mid-batch reads see progressively fresher data.
            _cache.Update(symbol, tick.Bid, tick.Ask, tick.Timestamp);

            // Keep only the latest tick per symbol for event publishing
            if (!latestBySymbol.TryGetValue(symbol, out var existing) || tick.Timestamp > existing.Timestamp)
                latestBySymbol[symbol] = tick;
        }

        // ── Step 4: Publish integration events ──────────────────────────────
        // One PriceUpdatedIntegrationEvent per symbol — consumers (StrategyWorker,
        // RiskMonitorWorker, TrailingStopWorker, etc.) subscribe to this event to trigger
        // their evaluation cycles. Using the latest tick ensures they see the most current price.
        foreach (var kvp in latestBySymbol)
        {
            var tick = kvp.Value;
            await _eventBus.SaveAndPublish(_context, new PriceUpdatedIntegrationEvent
            {
                Symbol       = kvp.Key,
                Bid          = tick.Bid,
                Ask          = tick.Ask,
                Timestamp    = tick.Timestamp,
                SpreadPoints = tick.SpreadPoints,
                TickVolume   = tick.TickVolume,
            });
        }

        // ── Step 5: Return result with telemetry ────────────────────────────
        // Include the dropped-tick count in the response so the EA can log/alert on excessive
        // staleness (e.g. if its network latency is consistently causing drops).
        // ── Metrics ───────────────────────────────────────────────────────────
        sw.Stop();
        _metrics.TicksIngested.Add(request.Ticks.Count, new KeyValuePair<string, object?>("instance", request.InstanceId));
        _metrics.TickBatchSize.Record(request.Ticks.Count, new KeyValuePair<string, object?>("instance", request.InstanceId));
        _metrics.TickIngestionLatencyMs.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("instance", request.InstanceId));

        string message = staleDropped > 0
            ? $"Successful ({staleDropped} stale/future tick(s) dropped)"
            : "Successful";
        return ResponseData<string>.Init(null, true, message, "00");
    }
}
