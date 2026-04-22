using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveDealSnapshot;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Receives a snapshot of recent deals (filled trades) from the EA.
/// Used to update order fill status and record execution details.
/// </summary>
public class ReceiveDealSnapshotCommand : IRequest<ResponseData<string>>
{
    /// <summary>Unique identifier of the EA instance providing the deal snapshot.</summary>
    public required string InstanceId { get; set; }

    /// <summary>List of recent deals (filled trades) to process.</summary>
    public List<DealSnapshotItem> Deals { get; set; } = new();
}

/// <summary>
/// Represents a single completed deal (trade fill) from MetaTrader 5's deal history.
/// </summary>
public class DealSnapshotItem
{
    /// <summary>Broker-assigned deal ticket number.</summary>
    public long     Ticket        { get; set; }

    /// <summary>Ticket of the order that generated this deal.</summary>
    public long     OrderTicket   { get; set; }

    /// <summary>Ticket of the position this deal belongs to.</summary>
    public long     PositionTicket { get; set; }

    /// <summary>Instrument symbol.</summary>
    public required string Symbol { get; set; }

    /// <summary>Deal direction: "Buy" or "Sell".</summary>
    public required string DealType { get; set; }

    /// <summary>Filled volume in lots.</summary>
    public decimal  Volume        { get; set; }

    /// <summary>Execution price of the deal.</summary>
    public decimal  Price         { get; set; }

    /// <summary>Realised profit/loss from this deal.</summary>
    public decimal  Profit        { get; set; }

    /// <summary>Broker commission charged for this deal.</summary>
    public decimal  Commission    { get; set; }

    /// <summary>Swap charges applied to this deal.</summary>
    public decimal  Swap          { get; set; }

    /// <summary>UTC time when the deal was executed.</summary>
    public DateTime DealTime      { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates InstanceId is non-empty and Deals is not null.
/// </summary>
public class ReceiveDealSnapshotCommandValidator : AbstractValidator<ReceiveDealSnapshotCommand>
{
    public ReceiveDealSnapshotCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Deals)
            .NotNull().WithMessage("Deals cannot be null")
            .Must(d => d.Count <= 500).WithMessage("Deal snapshot cannot exceed 500 items");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles deal snapshot processing. Matches each deal's OrderTicket to an existing engine Order
/// by BrokerOrderId. If the order is found and not yet filled, updates it with the fill price,
/// quantity, status (Filled), and fill timestamp from the deal.
/// </summary>
public class ReceiveDealSnapshotCommandHandler : IRequestHandler<ReceiveDealSnapshotCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public ReceiveDealSnapshotCommandHandler(IWriteApplicationDbContext context, IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(ReceiveDealSnapshotCommand request, CancellationToken cancellationToken)
    {
        if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
            return ResponseData<string>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        var dbContext = _context.GetDbContext();

        // Load the EA instance's owned symbols to validate each deal entry
        var eaInstance = await dbContext.Set<Domain.Entities.EAInstance>()
            .FirstOrDefaultAsync(x => x.InstanceId == request.InstanceId && !x.IsDeleted, cancellationToken);

        if (eaInstance is null)
            return ResponseData<string>.Init(null, false, "EA instance not found", "-14");

        var ownedSymbols = (eaInstance.Symbols ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant())
            .ToHashSet();

        // Hard-reject deals for symbols not owned by this EA instance
        foreach (var deal in request.Deals)
        {
            // Canonicalize via SymbolNormalizer (see ReceivePositionSnapshotCommand).
            var dealSymbol = LascodiaTradingEngine.Application.Common.Utilities.SymbolNormalizer.Normalize(deal.Symbol);
            if (ownedSymbols.Count > 0 && !ownedSymbols.Contains(dealSymbol))
                return ResponseData<string>.Init(null, false,
                    $"Symbol '{dealSymbol}' is not owned by EA instance '{request.InstanceId}'", "-403");
        }

        // Dedupe by deal ticket within the submitted snapshot. EAs occasionally re-emit
        // a deal when they replay a queue after a reconnect; we don't want two identical
        // Ticket values in the same snapshot to be processed twice. Deals are unique
        // broker-side by Ticket so using it as the idempotency key is authoritative.
        // We pick the later DealTime on collision in case the EA rebroadcast with
        // updated fill metadata, but status is gated by Order.Status below regardless.
        var dedupedDeals = request.Deals
            .GroupBy(d => d.Ticket)
            .Select(g => g.OrderBy(d => d.DealTime).Last())
            .ToList();

        // Batch-load matching orders to avoid N+1 queries
        var orderTickets = dedupedDeals.Select(d => d.OrderTicket.ToString()).Distinct().ToList();
        var matchingOrders = await dbContext
            .Set<Domain.Entities.Order>()
            .Where(x => orderTickets.Contains(x.BrokerOrderId!) && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        var orderByTicket = matchingOrders.ToDictionary(o => o.BrokerOrderId!);

        foreach (var deal in dedupedDeals)
        {
            var orderTicket = deal.OrderTicket.ToString();

            // The Status != Filled gate is the primary idempotency barrier — the first
            // deal for an order transitions it to Filled, and any subsequent deal (even
            // legitimately a second partial) is ignored here. Partial-fill aggregation
            // is a separate feature not yet wired; treat the first deal we see for an
            // order as the authoritative fill.
            if (orderByTicket.TryGetValue(orderTicket, out var order)
                && order.Status != OrderStatus.Filled)
            {
                order.FilledPrice    = deal.Price;
                order.FilledQuantity = deal.Volume;
                order.Status         = OrderStatus.Filled;
                order.FilledAt       = deal.DealTime;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
