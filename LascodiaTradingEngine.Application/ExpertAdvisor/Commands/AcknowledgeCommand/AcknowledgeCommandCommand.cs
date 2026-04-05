using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.AcknowledgeCommand;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Acknowledges an EA command that was previously queued for execution. The EA calls this after
/// processing (or failing to process) a command. Supports retry logic via the EACommand.ProcessAck method --
/// failed commands with remaining retries are automatically re-queued.
/// </summary>
public class AcknowledgeCommandCommand : IRequest<ResponseData<string>>
{
    /// <summary>Database ID of the EACommand to acknowledge.</summary>
    public long Id { get; set; }

    /// <summary>Execution outcome: "Success", "Failed", "TimedOut", or "Deferred".</summary>
    public string? Status { get; set; }

    /// <summary>Optional result details or error message from the EA's execution attempt.</summary>
    public string? Result { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates that Id is positive and Status is non-empty (must be one of Success, Failed, TimedOut, Deferred).
/// </summary>
public class AcknowledgeCommandCommandValidator : AbstractValidator<AcknowledgeCommandCommand>
{
    public AcknowledgeCommandCommandValidator()
    {
        RuleFor(x => x.Id)
            .GreaterThan(0).WithMessage("Command Id must be greater than zero");

        RuleFor(x => x.Status)
            .NotEmpty().WithMessage("Status is required (e.g. Success, Failed, TimedOut, Deferred)");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles command acknowledgement. Locates the EACommand by ID, rejects if already acknowledged (409),
/// then delegates to EACommand.ProcessAck which marks it complete or re-queues for retry on failure.
/// </summary>
public class AcknowledgeCommandCommandHandler : IRequestHandler<AcknowledgeCommandCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public AcknowledgeCommandCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(AcknowledgeCommandCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.EACommand>()
            .FirstOrDefaultAsync(
                x => x.Id == request.Id && !x.IsDeleted,
                cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Command not found", "-14");

        if (entity.Acknowledged)
            return ResponseData<string>.Init(null, false, "Command already acknowledged", "-409");

        bool wasRequeued = entity.ProcessAck(request.Status, request.Result);

        await _context.SaveChangesAsync(cancellationToken);

        if (wasRequeued)
            return ResponseData<string>.Init(null, true,
                $"Command re-queued for retry ({entity.RetryCount}/{Domain.Entities.EACommand.MaxRetries})", "00");

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
