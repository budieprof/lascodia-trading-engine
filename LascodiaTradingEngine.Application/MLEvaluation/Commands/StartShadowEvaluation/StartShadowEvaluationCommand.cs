using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLEvaluation.Commands.StartShadowEvaluation;

// ── Command ───────────────────────────────────────────────────────────────────

public class StartShadowEvaluationCommand : IRequest<ResponseData<long>>
{
    public long   ChallengerModelId { get; set; }
    public long   ChampionModelId   { get; set; }
    public required string Symbol    { get; set; }
    public required string Timeframe { get; set; }
    public int    RequiredTrades     { get; set; } = 50;
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class StartShadowEvaluationCommandValidator : AbstractValidator<StartShadowEvaluationCommand>
{
    public StartShadowEvaluationCommandValidator()
    {
        RuleFor(x => x.ChallengerModelId)
            .GreaterThan(0).WithMessage("ChallengerModelId must be greater than zero");

        RuleFor(x => x.ChampionModelId)
            .GreaterThan(0).WithMessage("ChampionModelId must be greater than zero");

        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol is required");

        RuleFor(x => x.Timeframe)
            .NotEmpty().WithMessage("Timeframe is required");

        RuleFor(x => x.RequiredTrades)
            .InclusiveBetween(10, 500).WithMessage("RequiredTrades must be between 10 and 500");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class StartShadowEvaluationCommandHandler : IRequestHandler<StartShadowEvaluationCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public StartShadowEvaluationCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(StartShadowEvaluationCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.MLShadowEvaluation
        {
            ChallengerModelId = request.ChallengerModelId,
            ChampionModelId   = request.ChampionModelId,
            Symbol            = request.Symbol.ToUpperInvariant(),
            Timeframe         = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true),
            Status            = ShadowEvaluationStatus.Running,
            RequiredTrades    = request.RequiredTrades,
            CompletedTrades   = 0,
            StartedAt         = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.MLShadowEvaluation>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
