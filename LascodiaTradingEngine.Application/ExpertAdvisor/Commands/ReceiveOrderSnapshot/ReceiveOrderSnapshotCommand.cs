using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveOrderSnapshot;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Receives a snapshot of pending orders from the broker via an EA instance.
/// Upserts engine Order records by matching on BrokerOrderId + Symbol.
/// Called during EA startup and periodically to synchronise pending order state.
/// </summary>
public class ReceiveOrderSnapshotCommand : IRequest<ResponseData<string>>
{
    /// <summary>Unique identifier of the EA instance providing the snapshot.</summary>
    public required string InstanceId { get; set; }

    /// <summary>List of pending orders as reported by the broker. Capped at 500 items.</summary>
    public List<OrderSnapshotItem> Orders { get; set; } = new();
}

/// <summary>
/// Represents a single pending order from the broker's perspective (MetaTrader 5 order data).
/// </summary>
public class OrderSnapshotItem
{
    /// <summary>Broker-assigned order ticket number.</summary>
    public long     Ticket        { get; set; }

    /// <summary>Instrument symbol (e.g. "EURUSD").</summary>
    public required string Symbol { get; set; }

    /// <summary>Order direction: "Buy" or "Sell".</summary>
    public required string OrderType  { get; set; }

    /// <summary>Execution method: "Limit", "Stop", or "StopLimit".</summary>
    public required string ExecutionType { get; set; }

    /// <summary>Order volume in lots.</summary>
    public decimal  Volume        { get; set; }

    /// <summary>Order trigger/limit price.</summary>
    public decimal  Price         { get; set; }

    /// <summary>Stop loss level attached to the order, if set.</summary>
    public decimal? StopLoss      { get; set; }

    /// <summary>Take profit level attached to the order, if set.</summary>
    public decimal? TakeProfit    { get; set; }

    /// <summary>UTC time when the order was placed on the broker.</summary>
    public DateTime PlacedTime    { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates InstanceId is non-empty, Orders is not null, and batch size does not exceed 500 items.
/// </summary>
public class ReceiveOrderSnapshotCommandValidator : AbstractValidator<ReceiveOrderSnapshotCommand>
{
    public ReceiveOrderSnapshotCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Orders)
            .NotNull().WithMessage("Orders cannot be null")
            .Must(o => o.Count <= 500).WithMessage("Order snapshot cannot exceed 500 items");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles order snapshot processing. For each order: updates price, volume, SL/TP on the existing
/// engine Order if matched by BrokerOrderId + Symbol, or creates a new Order record with Submitted
/// status for unknown broker orders.
/// </summary>
public class ReceiveOrderSnapshotCommandHandler : IRequestHandler<ReceiveOrderSnapshotCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public ReceiveOrderSnapshotCommandHandler(IWriteApplicationDbContext context, IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(ReceiveOrderSnapshotCommand request, CancellationToken cancellationToken)
    {
        if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
            return ResponseData<string>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        var dbContext = _context.GetDbContext();

        // Load the EA instance's owned symbols to validate each snapshot entry
        var eaInstance = await dbContext.Set<Domain.Entities.EAInstance>()
            .FirstOrDefaultAsync(x => x.InstanceId == request.InstanceId && !x.IsDeleted, cancellationToken);

        if (eaInstance is null)
            return ResponseData<string>.Init(null, false, "EA instance not found", "-14");

        var ownedSymbols = eaInstance.Symbols
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant())
            .ToHashSet();

        // Batch-load existing orders by broker ticket to avoid N+1 queries
        var brokerTickets = request.Orders.Select(o => o.Ticket.ToString()).Distinct().ToList();
        var existingOrders = await dbContext
            .Set<Domain.Entities.Order>()
            .Where(x => brokerTickets.Contains(x.BrokerOrderId!) && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        var orderLookup = existingOrders.ToDictionary(o => (o.BrokerOrderId!, o.Symbol));

        foreach (var snap in request.Orders)
        {
            var brokerTicket = snap.Ticket.ToString();
            // Canonicalize via SymbolNormalizer (see ReceivePositionSnapshotCommand).
            var symbol = LascodiaTradingEngine.Application.Common.Utilities.SymbolNormalizer.Normalize(snap.Symbol);

            // Hard-reject snapshots for symbols not owned by this EA instance to
            // prevent cross-instance data pollution and ensure strict symbol ownership.
            if (ownedSymbols.Count > 0 && !ownedSymbols.Contains(symbol))
                return ResponseData<string>.Init(null, false,
                    $"Symbol '{symbol}' is not owned by EA instance '{request.InstanceId}'", "-403");

            if (orderLookup.TryGetValue((brokerTicket, symbol), out var existing))
            {
                existing.Price      = snap.Price;
                existing.Quantity   = snap.Volume;
                existing.StopLoss   = snap.StopLoss;
                existing.TakeProfit = snap.TakeProfit;
            }
            else
            {
                if (!Enum.TryParse<OrderType>(snap.OrderType, ignoreCase: true, out var orderType)
                    || !Enum.TryParse<ExecutionType>(snap.ExecutionType, ignoreCase: true, out var executionType))
                    continue; // Skip orders with unrecognised type/execution values

                var newOrder = new Domain.Entities.Order
                {
                    Symbol        = symbol,
                    OrderType     = orderType,
                    ExecutionType = executionType,
                    Quantity      = snap.Volume,
                    Price         = snap.Price,
                    StopLoss      = snap.StopLoss,
                    TakeProfit    = snap.TakeProfit,
                    Status        = OrderStatus.Submitted,
                    BrokerOrderId = brokerTicket,
                    CreatedAt     = snap.PlacedTime,
                };
                await dbContext.Set<Domain.Entities.Order>().AddAsync(newOrder, cancellationToken);
                orderLookup[(brokerTicket, symbol)] = newOrder;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
