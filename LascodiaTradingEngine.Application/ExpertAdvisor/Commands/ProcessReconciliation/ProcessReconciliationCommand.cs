using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ProcessReconciliation;

// ── Command ───────────────────────────────────────────────────────────────────

public class ProcessReconciliationCommand : IRequest<ResponseData<ReconciliationResult>>
{
    public required string InstanceId { get; set; }
    public List<BrokerPositionItem> BrokerPositions { get; set; } = new();
    public List<BrokerOrderItem> BrokerOrders { get; set; } = new();
}

public class BrokerPositionItem
{
    public long     Ticket    { get; set; }
    public required string Symbol   { get; set; }
    public required string Direction { get; set; }
    public decimal  Volume    { get; set; }
    public decimal  OpenPrice { get; set; }
    public decimal? StopLoss  { get; set; }
    public decimal? TakeProfit { get; set; }
}

public class BrokerOrderItem
{
    public long     Ticket    { get; set; }
    public required string Symbol   { get; set; }
    public required string OrderType { get; set; }
    public decimal  Volume    { get; set; }
    public decimal  Price     { get; set; }
    public decimal? StopLoss  { get; set; }
    public decimal? TakeProfit { get; set; }
}

public class ReconciliationResult
{
    public int OrphanedEnginePositions { get; set; }
    public int UnknownBrokerPositions  { get; set; }
    public int MismatchedPositions     { get; set; }
    public int OrphanedEngineOrders    { get; set; }
    public int UnknownBrokerOrders     { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ProcessReconciliationCommandValidator : AbstractValidator<ProcessReconciliationCommand>
{
    public ProcessReconciliationCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ProcessReconciliationCommandHandler : IRequestHandler<ProcessReconciliationCommand, ResponseData<ReconciliationResult>>
{
    private readonly IWriteApplicationDbContext _context;

    public ProcessReconciliationCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<ReconciliationResult>> Handle(ProcessReconciliationCommand request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();
        var result = new ReconciliationResult();

        // ── Reconcile positions ──────────────────────────────────────────────
        var brokerTickets = request.BrokerPositions
            .Select(p => p.Ticket.ToString())
            .ToHashSet();

        var enginePositions = await dbContext
            .Set<Domain.Entities.Position>()
            .Where(x => x.Status == PositionStatus.Open && !x.IsDeleted)
            .ToListAsync(cancellationToken);

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
            if (!engineTickets.Contains(bp.Ticket.ToString()))
                result.UnknownBrokerPositions++;
        }

        // ── Reconcile orders ─────────────────────────────────────────────────
        var brokerOrderTickets = request.BrokerOrders
            .Select(o => o.Ticket.ToString())
            .ToHashSet();

        var engineOrders = await dbContext
            .Set<Domain.Entities.Order>()
            .Where(x => x.Status == OrderStatus.Submitted && !x.IsDeleted)
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
            if (!engineOrderTickets.Contains(bo.Ticket.ToString()))
                result.UnknownBrokerOrders++;
        }

        return ResponseData<ReconciliationResult>.Init(result, true, "Successful", "00");
    }
}
