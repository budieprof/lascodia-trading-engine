using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Commands.CreateStrategy;

// ── Command ───────────────────────────────────────────────────────────────────

public class CreateStrategyCommand : IRequest<ResponseData<long>>
{
    public required string Name           { get; set; }
    public required string Description    { get; set; }
    public required string StrategyType   { get; set; }
    public required string Symbol         { get; set; }
    public required string Timeframe      { get; set; }
    public string          ParametersJson { get; set; } = "{}";
    public long?           RiskProfileId  { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class CreateStrategyCommandValidator : AbstractValidator<CreateStrategyCommand>
{
    private static readonly string[] ValidStrategyTypes = ["MovingAverageCrossover", "RSIReversion", "BreakoutScalper", "Custom"];
    private static readonly string[] ValidTimeframes    = ["M1", "M5", "M15", "H1", "H4", "D1"];

    public CreateStrategyCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name cannot be empty")
            .MaximumLength(100).WithMessage("Name cannot exceed 100 characters");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description cannot be empty");

        RuleFor(x => x.StrategyType)
            .NotEmpty().WithMessage("StrategyType cannot be empty")
            .Must(t => ValidStrategyTypes.Contains(t))
            .WithMessage("StrategyType must be MovingAverageCrossover, RSIReversion, BreakoutScalper, or Custom");

        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty")
            .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters");

        RuleFor(x => x.Timeframe)
            .NotEmpty().WithMessage("Timeframe cannot be empty")
            .Must(t => ValidTimeframes.Contains(t))
            .WithMessage("Timeframe must be M1, M5, M15, H1, H4, or D1");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class CreateStrategyCommandHandler : IRequestHandler<CreateStrategyCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public CreateStrategyCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(CreateStrategyCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.Strategy
        {
            Name           = request.Name,
            Description    = request.Description,
            StrategyType   = Enum.Parse<Domain.Enums.StrategyType>(request.StrategyType, ignoreCase: true),
            Symbol         = request.Symbol,
            Timeframe      = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true),
            ParametersJson = request.ParametersJson,
            RiskProfileId  = request.RiskProfileId,
            Status         = StrategyStatus.Paused,
            CreatedAt      = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
