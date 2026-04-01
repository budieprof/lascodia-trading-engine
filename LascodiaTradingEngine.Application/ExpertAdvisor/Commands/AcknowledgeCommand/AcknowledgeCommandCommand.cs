using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.AcknowledgeCommand;

// ── Command ───────────────────────────────────────────────────────────────────

public class AcknowledgeCommandCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
    public string? Status { get; set; }
    public string? Result { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

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
