using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLEvaluation.Commands.StartShadowEvaluation;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Initiates a shadow evaluation that pits a challenger ML model against the current champion.
/// Both models score live signals in parallel; the MLShadowArbiterWorker compares their
/// prediction accuracy over the required number of trades before making a promotion decision.
/// </summary>
public class StartShadowEvaluationCommand : IRequest<ResponseData<long>>
{
    /// <summary>Database ID of the challenger model being evaluated for promotion.</summary>
    public long   ChallengerModelId { get; set; }

    /// <summary>Database ID of the current champion model being defended.</summary>
    public long   ChampionModelId   { get; set; }

    /// <summary>Instrument symbol for the evaluation (e.g. "EURUSD").</summary>
    public required string Symbol    { get; set; }

    /// <summary>Chart timeframe for the evaluation (e.g. "H1").</summary>
    public required string Timeframe { get; set; }

    /// <summary>Minimum number of completed trades before the arbiter can make a promotion decision. Defaults to 50.</summary>
    public int    RequiredTrades     { get; set; } = 50;
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates shadow evaluation parameters: positive model IDs, non-empty symbol/timeframe,
/// and RequiredTrades between 10 and 500.
/// </summary>
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

/// <summary>
/// Handles shadow evaluation creation. Inserts an MLShadowEvaluation record in Running status
/// with zero completed trades. The MLShadowArbiterWorker picks it up and tracks prediction
/// outcomes until the required trade count is reached.
/// </summary>
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
