using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceivePositionDelta;

// ── DTOs ─────────────────────────────────────────────────────────────────

/// <summary>
/// Represents a single position change event detected by the EA (opened, closed, or modified).
/// Only fields relevant to the action need to be populated; nulls are ignored during updates.
/// </summary>
public class PositionDeltaItem
{
    /// <summary>Type of change: "Opened", "Closed", or "Modified".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Broker-assigned position ticket number.</summary>
    public long Ticket { get; set; }

    /// <summary>Instrument symbol (required for Opened action).</summary>
    public string? Symbol { get; set; }

    /// <summary>Position direction: "Buy" (Long) or "Sell" (Short). Required for Opened action.</summary>
    public string? Type { get; set; }

    /// <summary>Current open volume in lots.</summary>
    public decimal? Volume { get; set; }

    /// <summary>Average entry price.</summary>
    public decimal? PriceOpen { get; set; }

    /// <summary>Current market price.</summary>
    public decimal? PriceCurrent { get; set; }

    /// <summary>Stop loss level.</summary>
    public decimal? StopLoss { get; set; }

    /// <summary>Take profit level.</summary>
    public decimal? TakeProfit { get; set; }

    /// <summary>Current unrealised profit/loss.</summary>
    public decimal? Profit { get; set; }

    /// <summary>Accumulated swap charges.</summary>
    public decimal? Swap { get; set; }

    /// <summary>Broker commission.</summary>
    public decimal? Commission { get; set; }

    /// <summary>EA magic number (used to identify which EA opened the position).</summary>
    public long? Magic { get; set; }

    /// <summary>Order comment set by the EA or broker.</summary>
    public string? Comment { get; set; }

    /// <summary>UTC time when the position was opened.</summary>
    public DateTime? OpenTime { get; set; }

    /// <summary>UTC time when the position was closed (for Closed action only).</summary>
    public DateTime? CloseTime { get; set; }
}

// ── Command ──────────────────────────────────────────────────────────────

/// <summary>
/// Receives incremental position change events (opened, closed, modified) from an EA instance.
/// Uses a monotonic sequence number for idempotency -- duplicate or out-of-order batches are skipped.
/// This is more efficient than full snapshots for real-time position tracking during active trading.
/// </summary>
public class ReceivePositionDeltaCommand : IRequest<ResponseData<int>>
{
    /// <summary>Unique identifier of the EA instance sending the deltas.</summary>
    public required string InstanceId { get; set; }

    /// <summary>Monotonic sequence number for idempotency. Batches with a sequence &lt;= the last processed value are skipped.</summary>
    public long SequenceNumber { get; set; }

    /// <summary>List of position change events in this batch.</summary>
    public List<PositionDeltaItem> Deltas { get; set; } = new();

    /// <summary>UTC time when this batch was assembled by the EA.</summary>
    public DateTime Timestamp { get; set; }
}

// ── Validator ────────────────────────────────────────────────────────────

/// <summary>
/// Validates InstanceId is non-empty, Deltas is non-empty, and each delta has a valid Action
/// ("Opened", "Closed", or "Modified") and a positive Ticket.
/// </summary>
public class ReceivePositionDeltaCommandValidator : AbstractValidator<ReceivePositionDeltaCommand>
{
    public ReceivePositionDeltaCommandValidator()
    {
        RuleFor(x => x.InstanceId).NotEmpty();
        RuleFor(x => x.Deltas).NotEmpty();
        RuleForEach(x => x.Deltas).ChildRules(item =>
        {
            item.RuleFor(x => x.Action).NotEmpty().Must(a => a == "Opened" || a == "Closed" || a == "Modified");
            item.RuleFor(x => x.Ticket).GreaterThan(0);
        });
    }
}

// ── Handler ──────────────────────────────────────────────────────────────

/// <summary>
/// Handles incremental position delta processing. Checks idempotency via the EAInstance's
/// LastProcessedDeltaSequence, then applies each delta: creates new Positions for "Opened",
/// closes existing Positions for "Closed", and updates SL/TP/volume/price for "Modified".
/// Updates the sequence watermark after successful processing.
/// </summary>
public class ReceivePositionDeltaCommandHandler : IRequestHandler<ReceivePositionDeltaCommand, ResponseData<int>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IReadApplicationDbContext _readContext;
    private readonly ILogger<ReceivePositionDeltaCommandHandler> _logger;

    public ReceivePositionDeltaCommandHandler(
        IWriteApplicationDbContext context,
        IReadApplicationDbContext readContext,
        ILogger<ReceivePositionDeltaCommandHandler> logger)
    {
        _context     = context;
        _readContext  = readContext;
        _logger      = logger;
    }

    public async Task<ResponseData<int>> Handle(ReceivePositionDeltaCommand request, CancellationToken cancellationToken)
    {
        // Idempotency: check if this exact (InstanceId, SequenceNumber) was already processed
        if (request.SequenceNumber > 0)
        {
            var readDb = _readContext.GetDbContext();
            var lastSeq = await readDb.Set<Domain.Entities.EAInstance>()
                .Where(e => e.InstanceId == request.InstanceId && !e.IsDeleted)
                .Select(e => e.LastProcessedDeltaSequence)
                .FirstOrDefaultAsync(cancellationToken);

            if (lastSeq.HasValue && request.SequenceNumber <= lastSeq.Value)
            {
                _logger.LogDebug(
                    "ReceivePositionDelta: duplicate sequence {Seq} for instance {Instance} (last processed: {Last}) — skipping",
                    request.SequenceNumber, request.InstanceId, lastSeq.Value);
                return ResponseData<int>.Init(0, true, "Duplicate sequence — already processed", "00");
            }
        }

        var db = _context.GetDbContext();
        int processed = 0;

        foreach (var delta in request.Deltas)
        {
            var brokerTicket = delta.Ticket.ToString();

            var position = await db.Set<Domain.Entities.Position>()
                .FirstOrDefaultAsync(p => p.BrokerPositionId == brokerTicket && !p.IsDeleted, cancellationToken);

            switch (delta.Action)
            {
                case "Opened":
                    if (position == null)
                    {
                        // Map EA direction string to PositionDirection enum
                        var direction = delta.Type == "Buy" ? PositionDirection.Long : PositionDirection.Short;
                        var symbol = (delta.Symbol ?? string.Empty).ToUpperInvariant();

                        var newPos = new Domain.Entities.Position
                        {
                            BrokerPositionId  = brokerTicket,
                            Symbol            = symbol,
                            Direction         = direction,
                            OpenLots          = delta.Volume ?? 0,
                            AverageEntryPrice = delta.PriceOpen ?? 0,
                            CurrentPrice      = delta.PriceCurrent ?? 0,
                            StopLoss          = delta.StopLoss,
                            TakeProfit        = delta.TakeProfit,
                            UnrealizedPnL     = delta.Profit ?? 0,
                            Swap              = delta.Swap ?? 0,
                            Commission        = delta.Commission ?? 0,
                            Status            = PositionStatus.Open,
                            OpenedAt          = delta.OpenTime ?? DateTime.UtcNow
                        };
                        db.Set<Domain.Entities.Position>().Add(newPos);
                        processed++;
                    }
                    break;

                case "Closed":
                    if (position != null && position.Status == PositionStatus.Open)
                    {
                        position.Status        = PositionStatus.Closed;
                        position.ClosedAt       = delta.CloseTime ?? DateTime.UtcNow;
                        position.UnrealizedPnL  = delta.Profit ?? position.UnrealizedPnL;
                        processed++;
                    }
                    break;

                case "Modified":
                    if (position != null && position.Status == PositionStatus.Open)
                    {
                        if (delta.StopLoss.HasValue)      position.StopLoss      = delta.StopLoss;
                        if (delta.TakeProfit.HasValue)     position.TakeProfit    = delta.TakeProfit;
                        if (delta.PriceCurrent.HasValue)   position.CurrentPrice  = delta.PriceCurrent.Value;
                        if (delta.Profit.HasValue)         position.UnrealizedPnL = delta.Profit.Value;
                        if (delta.Volume.HasValue)         position.OpenLots      = delta.Volume.Value;
                        if (delta.Swap.HasValue)           position.Swap          = delta.Swap.Value;
                        if (delta.Commission.HasValue)     position.Commission    = delta.Commission.Value;
                        processed++;
                    }
                    break;
            }
        }

        // Update the sequence watermark to prevent replaying this batch
        if (processed > 0 && request.SequenceNumber > 0)
        {
            await db.Set<Domain.Entities.EAInstance>()
                .Where(e => e.InstanceId == request.InstanceId && !e.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.LastProcessedDeltaSequence, request.SequenceNumber),
                    cancellationToken);
        }

        if (processed > 0)
            await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<int>.Init(processed, true, $"Processed {processed} position deltas", "00");
    }
}
