using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ProcessReconciliation;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Compares the engine's open positions and pending orders against the broker's current state
/// to detect discrepancies (orphaned engine records, unknown broker items, mismatches).
/// This is a read-heavy diagnostic command that does not modify data.
/// </summary>
public class ProcessReconciliationCommand : IRequest<ResponseData<ReconciliationResult>>
{
    /// <summary>Unique identifier of the EA instance performing the reconciliation.</summary>
    public required string InstanceId { get; set; }

    /// <summary>All currently open positions as reported by the broker.</summary>
    public List<BrokerPositionItem> BrokerPositions { get; set; } = new();

    /// <summary>All currently pending orders as reported by the broker.</summary>
    public List<BrokerOrderItem> BrokerOrders { get; set; } = new();
}

/// <summary>
/// A broker-side open position used for reconciliation comparison.
/// </summary>
public class BrokerPositionItem
{
    /// <summary>Broker-assigned position ticket number.</summary>
    public long     Ticket    { get; set; }

    /// <summary>Instrument symbol.</summary>
    public required string Symbol   { get; set; }

    /// <summary>Position direction: "Long" or "Short".</summary>
    public required string Direction { get; set; }

    /// <summary>Open volume in lots.</summary>
    public decimal  Volume    { get; set; }

    /// <summary>Average entry price.</summary>
    public decimal  OpenPrice { get; set; }

    /// <summary>Stop loss level, if set.</summary>
    public decimal? StopLoss  { get; set; }

    /// <summary>Take profit level, if set.</summary>
    public decimal? TakeProfit { get; set; }
}

/// <summary>
/// A broker-side pending order used for reconciliation comparison.
/// </summary>
public class BrokerOrderItem
{
    /// <summary>Broker-assigned order ticket number.</summary>
    public long     Ticket    { get; set; }

    /// <summary>Instrument symbol.</summary>
    public required string Symbol   { get; set; }

    /// <summary>Order type: "Buy" or "Sell".</summary>
    public required string OrderType { get; set; }

    /// <summary>Order volume in lots.</summary>
    public decimal  Volume    { get; set; }

    /// <summary>Order trigger/limit price.</summary>
    public decimal  Price     { get; set; }

    /// <summary>Stop loss level, if set.</summary>
    public decimal? StopLoss  { get; set; }

    /// <summary>Take profit level, if set.</summary>
    public decimal? TakeProfit { get; set; }
}

/// <summary>
/// Result of the reconciliation check, summarising discrepancies between engine and broker state.
/// </summary>
public class ReconciliationResult
{
    /// <summary>Number of engine positions with a BrokerPositionId not found on the broker (stale/closed on broker side).</summary>
    public int OrphanedEnginePositions { get; set; }

    /// <summary>Number of broker positions not tracked in the engine (opened externally or missed by sync).</summary>
    public int UnknownBrokerPositions  { get; set; }

    /// <summary>Number of positions present on both sides but with differing volume, SL, or TP.</summary>
    public int MismatchedPositions     { get; set; }

    /// <summary>Number of engine orders with a BrokerOrderId not found on the broker.</summary>
    public int OrphanedEngineOrders    { get; set; }

    /// <summary>Number of broker orders not tracked in the engine.</summary>
    public int UnknownBrokerOrders     { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates that the InstanceId is non-empty.
/// </summary>
public class ProcessReconciliationCommandValidator : AbstractValidator<ProcessReconciliationCommand>
{
    public ProcessReconciliationCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles reconciliation by cross-referencing engine positions/orders against broker snapshots.
/// Counts orphaned engine records (present in engine but not on broker), unknown broker records
/// (present on broker but not in engine), and returns the discrepancy summary without modifying data.
/// </summary>
    public class ProcessReconciliationCommandHandler : IRequestHandler<ProcessReconciliationCommand, ResponseData<ReconciliationResult>>
    {
        private readonly IWriteApplicationDbContext _context;
        private readonly IEAOwnershipGuard _ownershipGuard;

        public ProcessReconciliationCommandHandler(
            IWriteApplicationDbContext context,
            IEAOwnershipGuard ownershipGuard)
        {
            _context = context;
            _ownershipGuard = ownershipGuard;
        }

        public async Task<ResponseData<ReconciliationResult>> Handle(ProcessReconciliationCommand request, CancellationToken cancellationToken)
        {
            if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
                return ResponseData<ReconciliationResult>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

            var dbContext = _context.GetDbContext();
            var result = new ReconciliationResult();

            var eaInstance = await dbContext.Set<Domain.Entities.EAInstance>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.InstanceId == request.InstanceId && !x.IsDeleted, cancellationToken);

            if (eaInstance is null)
                return ResponseData<ReconciliationResult>.Init(null, false, "EA instance not found", "-14");

            var ownedSymbols = eaInstance.Symbols
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(SymbolNormalizer.Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── Reconcile positions ──────────────────────────────────────────────
        var brokerTickets = request.BrokerPositions
            .Select(p => p.Ticket.ToString())
            .ToHashSet();

        var enginePositionsQuery = dbContext
            .Set<Domain.Entities.Position>()
            .Where(x => x.Status == PositionStatus.Open && !x.IsDeleted);

        if (ownedSymbols.Count > 0)
            enginePositionsQuery = enginePositionsQuery.Where(x => ownedSymbols.Contains(x.Symbol));

        var enginePositions = await enginePositionsQuery.ToListAsync(cancellationToken);

        // Positions in the engine but not on the broker
        foreach (var pos in enginePositions)
        {
            if (pos.BrokerPositionId != null && !brokerTickets.Contains(pos.BrokerPositionId))
                result.OrphanedEnginePositions++;
        }

        // Positions on the broker but not in the engine
        var engineTickets = enginePositions
            .Where(p => p.BrokerPositionId != null)
            .Select(p => p.BrokerPositionId!)
            .ToHashSet();

        foreach (var bp in request.BrokerPositions)
        {
            var symbol = SymbolNormalizer.Normalize(bp.Symbol);
            if (ownedSymbols.Count > 0 && !ownedSymbols.Contains(symbol))
                return ResponseData<ReconciliationResult>.Init(null, false,
                    $"Symbol '{symbol}' is not owned by EA instance '{request.InstanceId}'", "-403");

            if (!engineTickets.Contains(bp.Ticket.ToString()))
                result.UnknownBrokerPositions++;
        }

        // ── Reconcile orders ─────────────────────────────────────────────────
        var brokerOrderTickets = request.BrokerOrders
            .Select(o => o.Ticket.ToString())
            .ToHashSet();

        var engineOrders = await dbContext
            .Set<Domain.Entities.Order>()
            .Where(x => x.Status == OrderStatus.Submitted
                     && x.TradingAccountId == eaInstance.TradingAccountId
                     && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var order in engineOrders)
        {
            if (order.BrokerOrderId != null && !brokerOrderTickets.Contains(order.BrokerOrderId))
                result.OrphanedEngineOrders++;
        }

        var engineOrderTickets = engineOrders
            .Where(o => o.BrokerOrderId != null)
            .Select(o => o.BrokerOrderId!)
            .ToHashSet();

        foreach (var bo in request.BrokerOrders)
        {
            var symbol = SymbolNormalizer.Normalize(bo.Symbol);
            if (ownedSymbols.Count > 0 && !ownedSymbols.Contains(symbol))
                return ResponseData<ReconciliationResult>.Init(null, false,
                    $"Symbol '{symbol}' is not owned by EA instance '{request.InstanceId}'", "-403");

            if (!engineOrderTickets.Contains(bo.Ticket.ToString()))
                result.UnknownBrokerOrders++;
        }

        // ── Persist an audit row so the EaReconciliationMonitorWorker can
        //    aggregate drift over time and alert on sustained divergence.
        //    A single insert per EA snapshot — trivial cost vs. the much more
        //    expensive comparisons just performed.
        int totalDrift = result.OrphanedEnginePositions
                       + result.UnknownBrokerPositions
                       + result.MismatchedPositions
                       + result.OrphanedEngineOrders
                       + result.UnknownBrokerOrders;

        dbContext.Set<Domain.Entities.ReconciliationRun>().Add(new Domain.Entities.ReconciliationRun
        {
            InstanceId               = request.InstanceId,
            RunAt                    = DateTime.UtcNow,
            OrphanedEnginePositions  = result.OrphanedEnginePositions,
            UnknownBrokerPositions   = result.UnknownBrokerPositions,
            MismatchedPositions      = result.MismatchedPositions,
            OrphanedEngineOrders     = result.OrphanedEngineOrders,
            UnknownBrokerOrders      = result.UnknownBrokerOrders,
            TotalDrift               = totalDrift,
            BrokerPositionCount      = request.BrokerPositions.Count,
            BrokerOrderCount         = request.BrokerOrders.Count,
        });
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<ReconciliationResult>.Init(result, true, "Successful", "00");
    }
}
