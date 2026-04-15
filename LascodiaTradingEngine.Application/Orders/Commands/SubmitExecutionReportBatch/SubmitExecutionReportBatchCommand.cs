using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.SubmitExecutionReportBatch;

// ── DTO ──────────────────────────────────────────────────────────────────────

/// <summary>
/// A single execution report within a batch, describing the broker-side outcome for one order.
/// Accepts all 19 fields sent by the EA for full execution telemetry.
/// </summary>
public class ExecutionReportItem
{
    /// <summary>Engine-side order identifier.</summary>
    public long     OrderId         { get; set; }
    /// <summary>Broker-assigned order ticket (MT5 order ticket as string).</summary>
    public string?  BrokerOrderId   { get; set; }
    /// <summary>Actual fill price from the broker.</summary>
    public decimal? FilledPrice     { get; set; }
    /// <summary>Actual filled quantity from the broker.</summary>
    public decimal? FilledQuantity  { get; set; }
    /// <summary>Execution outcome: "Filled", "Rejected", "Cancelled", "PartialFill", or "Failed".</summary>
    public required string Status   { get; set; }
    /// <summary>Reason the order was rejected, if applicable.</summary>
    public string?  RejectionReason { get; set; }
    /// <summary>Timestamp when the fill occurred at the broker.</summary>
    public DateTime? FilledAt       { get; set; }

    // ── EA telemetry fields (accepted but not persisted to Order entity) ────

    /// <summary>Trade signal ID that triggered this order.</summary>
    public long?    SignalId           { get; set; }
    /// <summary>MT5 deal ticket for the fill transaction.</summary>
    public long?    Mt5DealTicket      { get; set; }
    /// <summary>EA magic number (unique per EA instance).</summary>
    public long?    MagicNumber        { get; set; }
    /// <summary>Price originally requested by the signal/order.</summary>
    public decimal? RequestedPrice     { get; set; }
    /// <summary>Signed slippage in pips (positive = adverse, negative = improvement).</summary>
    public decimal? SlippagePips       { get; set; }
    /// <summary>Slippage in broker points.</summary>
    public int?     SlippagePoints     { get; set; }
    /// <summary>Commission charged by the broker for this fill.</summary>
    public decimal? Commission         { get; set; }
    /// <summary>Elapsed ms from OrderSend() to fill confirmation at the broker.</summary>
    public int?     ExecutionLatencyMs { get; set; }
    /// <summary>Elapsed ms the order spent in the EA order queue before execution.</summary>
    public int?     QueueDwellMs       { get; set; }
    /// <summary>Fill policy used by MT5 (FOK, IOC, RETURN).</summary>
    public string?  FillPolicy         { get; set; }
    /// <summary>Account margin mode (hedging, netting).</summary>
    public string?  AccountMode        { get; set; }
    /// <summary>Broker return code from OrderSend result.</summary>
    public int?     BrokerRetcode      { get; set; }
}

// ── Result DTO (per-report status) ───────────────────────────────────────────

/// <summary>Aggregate result of processing an execution report batch.</summary>
public class ExecutionReportBatchResult
{
    /// <summary>Number of reports successfully applied.</summary>
    public int Processed { get; set; }
    /// <summary>Number of reports skipped (order not found or invalid status).</summary>
    public int Skipped { get; set; }
    /// <summary>Per-report processing results.</summary>
    public List<ExecutionReportItemResult> Items { get; set; } = new();
}

/// <summary>Processing result for a single execution report within the batch.</summary>
public class ExecutionReportItemResult
{
    /// <summary>Engine-side order identifier.</summary>
    public long OrderId { get; set; }
    /// <summary>Whether the report was applied successfully.</summary>
    public bool Success { get; set; }
    /// <summary>Failure reason if the report was skipped.</summary>
    public string? Reason { get; set; }
}

// ── Command ──────────────────────────────────────────────────────────────────

/// <summary>
/// Submits a batch of execution reports from the EA, updating order statuses and fill
/// details in bulk. Publishes <see cref="OrderFilledIntegrationEvent"/> for each newly filled order.
/// </summary>
public class SubmitExecutionReportBatchCommand : IRequest<ResponseData<ExecutionReportBatchResult>>
{
    /// <summary>Collection of execution reports to process (max 100).</summary>
    public List<ExecutionReportItem> Reports { get; set; } = new();
}

// ── Validator ────────────────────────────────────────────────────────────────

/// <summary>Validates the batch size limit (100) and each report's OrderId and Status.</summary>
public class SubmitExecutionReportBatchCommandValidator : AbstractValidator<SubmitExecutionReportBatchCommand>
{
    public SubmitExecutionReportBatchCommandValidator()
    {
        RuleFor(x => x.Reports)
            .NotNull().WithMessage("Reports list cannot be null")
            .Must(r => r.Count <= 100).WithMessage("Execution report batch cannot exceed 100 items");

        RuleForEach(x => x.Reports).ChildRules(report =>
        {
            report.RuleFor(r => r.OrderId)
                .GreaterThan(0).WithMessage("Order Id must be greater than zero");

            report.RuleFor(r => r.Status)
                .NotEmpty().WithMessage("Status cannot be empty")
                .Must(s => s is "Filled" or "Rejected" or "Cancelled" or "PartialFill" or "Failed")
                .WithMessage("Status must be Filled, Rejected, Cancelled, PartialFill, or Failed");
        });
    }
}

// ── Handler ──────────────────────────────────────────────────────────────────

/// <summary>
/// Iterates over each execution report, updating the corresponding order's status and fill
/// details. Publishes an <see cref="OrderFilledIntegrationEvent"/> on first transition to Filled.
/// </summary>
public class SubmitExecutionReportBatchCommandHandler : IRequestHandler<SubmitExecutionReportBatchCommand, ResponseData<ExecutionReportBatchResult>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;

    public SubmitExecutionReportBatchCommandHandler(IWriteApplicationDbContext context, IIntegrationEventService eventBus)
    {
        _context  = context;
        _eventBus = eventBus;
    }

    public async Task<ResponseData<ExecutionReportBatchResult>> Handle(SubmitExecutionReportBatchCommand request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();
        var result = new ExecutionReportBatchResult();

        foreach (var report in request.Reports)
        {
            var entity = await dbContext
                .Set<Domain.Entities.Order>()
                .FirstOrDefaultAsync(
                    x => x.Id == report.OrderId && !x.IsDeleted,
                    cancellationToken);

            if (entity == null)
            {
                result.Skipped++;
                result.Items.Add(new ExecutionReportItemResult
                    { OrderId = report.OrderId, Success = false, Reason = "Order not found" });
                continue;
            }

            if (!Enum.TryParse<OrderStatus>(report.Status, ignoreCase: true, out var newStatus))
            {
                result.Skipped++;
                result.Items.Add(new ExecutionReportItemResult
                    { OrderId = report.OrderId, Success = false, Reason = $"Invalid status: {report.Status}" });
                continue;
            }

            var previousStatus = entity.Status;
            entity.Status          = newStatus;
            entity.BrokerOrderId   = report.BrokerOrderId ?? entity.BrokerOrderId;
            entity.FilledPrice     = report.FilledPrice ?? entity.FilledPrice;
            entity.FilledQuantity  = report.FilledQuantity ?? entity.FilledQuantity;
            entity.RejectionReason = report.RejectionReason ?? entity.RejectionReason;
            entity.FilledAt        = report.FilledAt ?? entity.FilledAt;

            if (newStatus == OrderStatus.Filled && previousStatus != OrderStatus.Filled)
            {
                var filledPrice = entity.FilledPrice ?? 0;
                var filledQty   = entity.FilledQuantity ?? 0;
                var fillRate    = entity.Quantity > 0 ? filledQty / entity.Quantity : 1m;

                await _eventBus.SaveAndPublish(_context, new OrderFilledIntegrationEvent
                {
                    OrderId            = entity.Id,
                    StrategyId         = entity.StrategyId,
                    Symbol             = entity.Symbol,
                    Session            = entity.Session,
                    RequestedPrice     = report.RequestedPrice ?? entity.Price,
                    FilledPrice        = filledPrice,
                    WasPartialFill     = fillRate < 1m,
                    FillRate           = fillRate,
                    FilledAt           = entity.FilledAt ?? DateTime.UtcNow,
                    SubmitToFillMs     = report.ExecutionLatencyMs ?? 0,
                    SlippagePips       = report.SlippagePips,
                    Commission         = report.Commission,
                    QueueDwellMs       = report.QueueDwellMs,
                    BrokerRetcode      = report.BrokerRetcode,
                    BrokerOrderId      = entity.BrokerOrderId,
                });
            }

            result.Processed++;
            result.Items.Add(new ExecutionReportItemResult
                { OrderId = report.OrderId, Success = true });
        }

        await _context.SaveChangesAsync(cancellationToken);
        return ResponseData<ExecutionReportBatchResult>.Init(result, true, "Successful", "00");
    }
}
