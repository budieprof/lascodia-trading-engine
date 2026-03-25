using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ProcessSignalFeedback;

// ── DTO ──────────────────────────────────────────────────────────────────

public class SignalFeedbackItem
{
    public long SignalId { get; set; }
    public string Reason { get; set; } = string.Empty;  // "Expired", "SpreadDeferred", "Dropped", "SafetyRejected", "MarketClosed", "SymbolNotFound"
    public string? Details { get; set; }
    public DateTime Timestamp { get; set; }
}

// ── Command ──────────────────────────────────────────────────────────────

public class ProcessSignalFeedbackCommand : IRequest<ResponseData<int>>
{
    public required string InstanceId { get; set; }
    public List<SignalFeedbackItem> Feedback { get; set; } = new();
}

// ── Validator ────────────────────────────────────────────────────────────

public class ProcessSignalFeedbackCommandValidator : AbstractValidator<ProcessSignalFeedbackCommand>
{
    public ProcessSignalFeedbackCommandValidator()
    {
        RuleFor(x => x.InstanceId).NotEmpty().WithMessage("InstanceId cannot be empty");
        RuleFor(x => x.Feedback).NotEmpty().WithMessage("Feedback list cannot be empty");
        RuleForEach(x => x.Feedback).ChildRules(item =>
        {
            item.RuleFor(x => x.SignalId).GreaterThan(0);
            item.RuleFor(x => x.Reason).NotEmpty();
        });
    }
}

// ── Handler ──────────────────────────────────────────────────────────────

public class ProcessSignalFeedbackCommandHandler : IRequestHandler<ProcessSignalFeedbackCommand, ResponseData<int>>
{
    private readonly IWriteApplicationDbContext _context;

    public ProcessSignalFeedbackCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<int>> Handle(ProcessSignalFeedbackCommand request, CancellationToken cancellationToken)
    {
        int processed = 0;
        var db = _context.GetDbContext();

        // Get all signal IDs from the feedback batch
        var signalIds = request.Feedback.Select(f => f.SignalId).Distinct().ToList();

        // Batch-load signals that are still in actionable states
        var signals = await db.Set<Domain.Entities.TradeSignal>()
            .Where(s => signalIds.Contains(s.Id) && !s.IsDeleted)
            .ToListAsync(cancellationToken);

        var signalMap = signals.ToDictionary(s => s.Id);

        foreach (var item in request.Feedback)
        {
            if (!signalMap.TryGetValue(item.SignalId, out var signal))
                continue;

            // Only update signals that are still pending or approved (not already executed/rejected/expired)
            if (signal.Status != TradeSignalStatus.Pending && signal.Status != TradeSignalStatus.Approved)
                continue;

            switch (item.Reason)
            {
                case "Expired":
                case "MarketClosed":
                    signal.Status = TradeSignalStatus.Expired;
                    signal.RejectionReason = $"EA: {item.Reason}" + (item.Details != null ? $" - {item.Details}" : "");
                    processed++;
                    break;

                case "Dropped":
                case "SafetyRejected":
                case "SymbolNotFound":
                    signal.Status = TradeSignalStatus.Rejected;
                    signal.RejectionReason = $"EA: {item.Reason}" + (item.Details != null ? $" - {item.Details}" : "");
                    processed++;
                    break;

                case "SpreadDeferred":
                    // Don't change status - just log the deferral. Signal stays in Pending/Approved.
                    // Engine can use this for monitoring but the EA may still execute it.
                    break;
            }
        }

        if (processed > 0)
            await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<int>.Init(processed, true, $"Processed {processed} signal feedback items", "00");
    }
}
