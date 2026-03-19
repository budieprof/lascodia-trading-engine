using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Brokers.Commands.UpdateBrokerStatus;

// ── Command ───────────────────────────────────────────────────────────────────

public class UpdateBrokerStatusCommand : IRequest<ResponseData<string>>
{
    public long    Id            { get; set; }
    public string  Status        { get; set; } = string.Empty;
    public string? StatusMessage { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class UpdateBrokerStatusCommandValidator : AbstractValidator<UpdateBrokerStatusCommand>
{
    public UpdateBrokerStatusCommandValidator()
    {
        RuleFor(x => x.Status)
            .Must(s => Enum.TryParse<BrokerStatus>(s, ignoreCase: true, out _))
            .WithMessage("Status must be one of: Connected, Disconnected, Error");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class UpdateBrokerStatusCommandHandler : IRequestHandler<UpdateBrokerStatusCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpdateBrokerStatusCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(UpdateBrokerStatusCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Broker>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Broker not found", "-14");

        entity.Status        = Enum.Parse<BrokerStatus>(request.Status, ignoreCase: true);
        entity.StatusMessage = request.StatusMessage;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Updated", true, "Successful", "00");
    }
}
