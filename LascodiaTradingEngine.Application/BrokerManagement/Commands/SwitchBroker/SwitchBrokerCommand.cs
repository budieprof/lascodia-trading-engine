using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.BrokerManagement.Commands.SwitchBroker;

// ── Command ───────────────────────────────────────────────────────────────────

public class SwitchBrokerCommand : IRequest<ResponseData<string>>
{
    public required string BrokerName { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class SwitchBrokerCommandValidator : AbstractValidator<SwitchBrokerCommand>
{
    private static readonly string[] AllowedBrokers = ["oanda", "ib", "paper"];

    public SwitchBrokerCommandValidator()
    {
        RuleFor(x => x.BrokerName)
            .NotEmpty().WithMessage("BrokerName is required")
            .Must(b => AllowedBrokers.Contains(b.ToLowerInvariant()))
            .WithMessage("BrokerName must be one of: oanda, ib, paper");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class SwitchBrokerCommandHandler : IRequestHandler<SwitchBrokerCommand, ResponseData<string>>
{
    private readonly IBrokerFailover _brokerFailover;
    private readonly IWriteApplicationDbContext _context;

    public SwitchBrokerCommandHandler(IBrokerFailover brokerFailover, IWriteApplicationDbContext context)
    {
        _brokerFailover = brokerFailover;
        _context        = context;
    }

    public async Task<ResponseData<string>> Handle(
        SwitchBrokerCommand request, CancellationToken cancellationToken)
    {
        await _brokerFailover.SwitchBrokerAsync(request.BrokerName, cancellationToken);

        var db = _context.GetDbContext();

        var config = await db.Set<Domain.Entities.EngineConfig>()
            .FirstOrDefaultAsync(x => x.Key == "BrokerType" && !x.IsDeleted, cancellationToken);

        if (config is not null)
        {
            config.Value         = request.BrokerName;
            config.LastUpdatedAt = DateTime.UtcNow;
        }
        else
        {
            await db.Set<Domain.Entities.EngineConfig>().AddAsync(new Domain.Entities.EngineConfig
            {
                Key           = "BrokerType",
                Value         = request.BrokerName,
                Description   = "Active broker adapter",
                DataType      = ConfigDataType.String,
                LastUpdatedAt = DateTime.UtcNow
            }, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(request.BrokerName, true, "Broker switched successfully", "00");
    }
}
