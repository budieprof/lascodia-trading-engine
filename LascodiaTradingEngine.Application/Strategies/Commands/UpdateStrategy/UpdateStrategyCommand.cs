using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Commands.UpdateStrategy;

// ── Command ───────────────────────────────────────────────────────────────────

public class UpdateStrategyCommand : IRequest<ResponseData<string>>
{
    [JsonIgnore] public long Id { get; set; }

    public string? Name           { get; set; }
    public string? Description    { get; set; }
    public string? StrategyType   { get; set; }
    public string? Symbol         { get; set; }
    public string? Timeframe      { get; set; }
    public string? ParametersJson { get; set; }
    public long?   RiskProfileId  { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class UpdateStrategyCommandValidator : AbstractValidator<UpdateStrategyCommand>
{
    private static readonly string[] ValidStrategyTypes = ["MovingAverageCrossover", "RSIReversion", "BreakoutScalper", "Custom"];
    private static readonly string[] ValidTimeframes    = ["M1", "M5", "M15", "H1", "H4", "D1"];

    public UpdateStrategyCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Strategy Id is required");

        When(x => x.Name != null, () =>
            RuleFor(x => x.Name)
                .NotEmpty().WithMessage("Name cannot be empty")
                .MaximumLength(100).WithMessage("Name cannot exceed 100 characters"));

        When(x => x.StrategyType != null, () =>
            RuleFor(x => x.StrategyType)
                .Must(t => ValidStrategyTypes.Contains(t))
                .WithMessage("StrategyType must be MovingAverageCrossover, RSIReversion, BreakoutScalper, or Custom"));

        When(x => x.Symbol != null, () =>
            RuleFor(x => x.Symbol)
                .NotEmpty().WithMessage("Symbol cannot be empty")
                .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters"));

        When(x => x.Timeframe != null, () =>
            RuleFor(x => x.Timeframe)
                .Must(t => ValidTimeframes.Contains(t))
                .WithMessage("Timeframe must be M1, M5, M15, H1, H4, or D1"));
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class UpdateStrategyCommandHandler : IRequestHandler<UpdateStrategyCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpdateStrategyCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(UpdateStrategyCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Strategy not found", "-14");

        if (!string.IsNullOrWhiteSpace(request.Name))           entity.Name           = request.Name;
        if (!string.IsNullOrWhiteSpace(request.Description))    entity.Description    = request.Description;
        if (!string.IsNullOrWhiteSpace(request.StrategyType))   entity.StrategyType   = Enum.Parse<Domain.Enums.StrategyType>(request.StrategyType, ignoreCase: true);
        if (!string.IsNullOrWhiteSpace(request.Symbol))         entity.Symbol         = request.Symbol;
        if (!string.IsNullOrWhiteSpace(request.Timeframe))      entity.Timeframe      = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true);
        if (request.ParametersJson != null)                      entity.ParametersJson = request.ParametersJson;
        if (request.RiskProfileId.HasValue)                      entity.RiskProfileId  = request.RiskProfileId;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Updated", true, "Successful", "00");
    }
}
