using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.DeregisterEA;

// ── Command ───────────────────────────────────────────────────────────────────

public class DeregisterEACommand : IRequest<ResponseData<string>>
{
    public required string InstanceId { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class DeregisterEACommandValidator : AbstractValidator<DeregisterEACommand>
{
    public DeregisterEACommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class DeregisterEACommandHandler : IRequestHandler<DeregisterEACommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public DeregisterEACommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(DeregisterEACommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.EAInstance>()
            .FirstOrDefaultAsync(
                x => x.InstanceId == request.InstanceId
                  && x.Status != EAInstanceStatus.ShuttingDown
                  && !x.IsDeleted,
                cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "EA instance not found", "-14");

        entity.Status         = EAInstanceStatus.ShuttingDown;
        entity.DeregisteredAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
