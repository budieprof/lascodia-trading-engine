using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;

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

    /// <summary>
    /// Client-supplied idempotency token (usually a GUID) that stays constant
    /// across EA retries of the same execution. The handler returns the stored
    /// result on duplicate submissions with the same token on an already-
    /// acknowledged command, preventing the EA from treating a legitimate
    /// retry as a hard failure when the network drops the first ACK response.
    /// Null → the legacy (non-idempotent) path: a second ACK returns 409.
    /// </summary>
    public string? ClientAckToken { get; set; }
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
    private readonly IEAOwnershipGuard _ownershipGuard;

    public AcknowledgeCommandCommandHandler(
        IWriteApplicationDbContext context,
        IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _ownershipGuard = ownershipGuard;
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

        if (!await _ownershipGuard.IsOwnerAsync(entity.TargetInstanceId, cancellationToken))
            return ResponseData<string>.Init(null, false, "Unauthorized", "-403");

        // Idempotency: a duplicate ACK carrying the same ClientAckToken on an
        // already-acknowledged command is a network retry from the EA side —
        // return the stored result (success, not 409) so the EA treats the
        // duplicate as benign. A different token on an already-acknowledged
        // command still conflicts because it implies a different execution
        // attempt on the same command ID, which the engine must refuse.
        if (entity.Acknowledged)
        {
            bool tokensMatch =
                !string.IsNullOrWhiteSpace(request.ClientAckToken)
                && string.Equals(entity.ClientAckToken, request.ClientAckToken, StringComparison.Ordinal);

            if (tokensMatch)
                return ResponseData<string>.Init(entity.AckResult, true,
                    "Acknowledged (idempotent replay)", "00");

            return ResponseData<string>.Init(null, false, "Command already acknowledged", "-409");
        }

        // First-time ACK: record the token so subsequent retries can match.
        if (!string.IsNullOrWhiteSpace(request.ClientAckToken))
            entity.ClientAckToken = request.ClientAckToken;

        bool wasRequeued = entity.ProcessAck(request.Status, request.Result);

        await _context.SaveChangesAsync(cancellationToken);

        if (wasRequeued)
            return ResponseData<string>.Init(null, true,
                $"Command re-queued for retry ({entity.RetryCount}/{Domain.Entities.EACommand.MaxRetries})", "00");

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
