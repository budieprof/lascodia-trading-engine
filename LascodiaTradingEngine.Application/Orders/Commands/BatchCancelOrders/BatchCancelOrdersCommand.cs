using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Orders.Commands.CancelOrder;

namespace LascodiaTradingEngine.Application.Orders.Commands.BatchCancelOrders;

// ── Command + result types ────────────────────────────────────────────────────

/// <summary>
/// Cancels multiple orders in a single request, returning a per-order result so the
/// admin UI can render success/failure on each row. Best-effort, not transactional —
/// each cancel is independent. Capped at 50 ids per call to keep the EA-command queue
/// from being flooded by a single operator click.
/// </summary>
public class BatchCancelOrdersCommand : IRequest<ResponseData<BatchCancelOrdersResult>>
{
    /// <summary>Order ids to cancel. Duplicates are coalesced. Cap: 50.</summary>
    public List<long> OrderIds { get; set; } = new();

    /// <summary>Free-form reason recorded against the batch for audit/forensics.</summary>
    public string? Reason { get; set; }
}

/// <summary>Per-order outcome for a batch-cancel request.</summary>
public class BatchCancelOrdersItem
{
    /// <summary>Order id whose cancel was attempted.</summary>
    public long Id { get; set; }

    /// <summary><c>"Cancelled"</c> on success, <c>"Failed"</c> when the underlying cancel returned a non-success envelope.</summary>
    public string Status { get; set; } = "Failed";

    /// <summary>Error message from the underlying cancel handler when <see cref="Status"/> is <c>"Failed"</c>.</summary>
    public string? Reason { get; set; }
}

/// <summary>Aggregate counts plus the per-order detail array.</summary>
public class BatchCancelOrdersResult
{
    /// <summary>Number of unique order ids attempted.</summary>
    public int Total     { get; set; }

    /// <summary>Number of orders the engine successfully cancelled.</summary>
    public int Cancelled { get; set; }

    /// <summary>Number of orders the engine could not cancel.</summary>
    public int Failed    { get; set; }

    /// <summary>One row per attempted order, in input order, deduplicated.</summary>
    public List<BatchCancelOrdersItem> Results { get; set; } = new();
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Caps the batch at 50 ids and rejects empty/non-positive ids early. Anything larger
/// than 50 is a scripting job rather than an operator click.
/// </summary>
public class BatchCancelOrdersCommandValidator : AbstractValidator<BatchCancelOrdersCommand>
{
    public const int MaxBatch = 50;

    public BatchCancelOrdersCommandValidator()
    {
        RuleFor(x => x.OrderIds).NotEmpty().WithMessage("OrderIds must not be empty");
        RuleFor(x => x.OrderIds.Count).LessThanOrEqualTo(MaxBatch)
            .WithMessage($"OrderIds count must not exceed {MaxBatch}");
        RuleForEach(x => x.OrderIds).GreaterThan(0).WithMessage("Each OrderId must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Iterates the (deduplicated) id list, dispatching <see cref="CancelOrderCommand"/> per id.
/// Reuses the single-cancel handler so ownership, status, EA-command-queue, and audit logic
/// stay in one place.
/// </summary>
public class BatchCancelOrdersCommandHandler
    : IRequestHandler<BatchCancelOrdersCommand, ResponseData<BatchCancelOrdersResult>>
{
    private readonly ISender _mediator;

    public BatchCancelOrdersCommandHandler(ISender mediator)
    {
        _mediator = mediator;
    }

    public async Task<ResponseData<BatchCancelOrdersResult>> Handle(
        BatchCancelOrdersCommand request, CancellationToken cancellationToken)
    {
        var ids = request.OrderIds.Distinct().ToList();
        var result = new BatchCancelOrdersResult
        {
            Total   = ids.Count,
            Results = new List<BatchCancelOrdersItem>(ids.Count),
        };

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var perItem = await _mediator.Send(new CancelOrderCommand { Id = id }, cancellationToken);
            var ok = perItem?.status == true;

            result.Results.Add(new BatchCancelOrdersItem
            {
                Id     = id,
                Status = ok ? "Cancelled" : "Failed",
                Reason = ok ? null : perItem?.message,
            });

            if (ok) result.Cancelled++;
            else    result.Failed++;
        }

        var msg = $"Batch complete: {result.Cancelled} cancelled, {result.Failed} failed";
        return ResponseData<BatchCancelOrdersResult>.Init(result, true, msg, "00");
    }
}
