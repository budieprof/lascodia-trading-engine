using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Brokers.Commands.CreateBroker;

// ── Command ───────────────────────────────────────────────────────────────────

public class CreateBrokerCommand : IRequest<ResponseData<long>>
{
    public required string  Name        { get; set; }
    public required string  BrokerType  { get; set; }
    public string           Environment { get; set; } = "Practice";
    public required string  BaseUrl     { get; set; }
    public string?          ApiKey      { get; set; }
    public string?          ApiSecret   { get; set; }
    public bool             IsPaper     { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class CreateBrokerCommandValidator : AbstractValidator<CreateBrokerCommand>
{
    public CreateBrokerCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name cannot be empty")
            .MaximumLength(100).WithMessage("Name cannot exceed 100 characters");

        RuleFor(x => x.BrokerType)
            .NotEmpty().WithMessage("BrokerType cannot be empty")
            .Must(t => Enum.TryParse<BrokerType>(t, ignoreCase: true, out _))
            .WithMessage("BrokerType must be one of: Oanda, IB, Paper");

        RuleFor(x => x.Environment)
            .Must(e => Enum.TryParse<BrokerEnvironment>(e, ignoreCase: true, out _))
            .WithMessage("Environment must be one of: Live, Practice");

        RuleFor(x => x.BaseUrl)
            .NotEmpty().WithMessage("BaseUrl cannot be empty");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class CreateBrokerCommandHandler : IRequestHandler<CreateBrokerCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public CreateBrokerCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(CreateBrokerCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.Broker
        {
            Name        = request.Name,
            BrokerType  = Enum.Parse<BrokerType>(request.BrokerType, ignoreCase: true),
            Environment = Enum.Parse<BrokerEnvironment>(request.Environment, ignoreCase: true),
            BaseUrl     = request.BaseUrl,
            ApiKey      = request.ApiKey,
            ApiSecret   = request.ApiSecret,
            IsPaper     = request.IsPaper,
            IsActive    = false,
            Status      = BrokerStatus.Disconnected
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.Broker>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
