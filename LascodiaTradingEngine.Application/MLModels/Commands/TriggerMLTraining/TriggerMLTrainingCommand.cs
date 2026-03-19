using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Commands.TriggerMLTraining;

// ── Command ───────────────────────────────────────────────────────────────────

public class TriggerMLTrainingCommand : IRequest<ResponseData<long>>
{
    public required string Symbol    { get; set; }
    public required string Timeframe { get; set; }
    public DateTime        FromDate  { get; set; }
    public DateTime        ToDate    { get; set; }
    public string          TriggerType { get; set; } = "Manual";
    /// <summary>
    /// The trainer architecture to use for this run.
    /// Defaults to <see cref="LearnerArchitecture.BaggedLogistic"/> when not specified.
    /// </summary>
    public LearnerArchitecture LearnerArchitecture { get; set; } = LearnerArchitecture.BaggedLogistic;
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class TriggerMLTrainingCommandValidator : AbstractValidator<TriggerMLTrainingCommand>
{
    public TriggerMLTrainingCommandValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty")
            .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters");

        RuleFor(x => x.Timeframe)
            .NotEmpty().WithMessage("Timeframe cannot be empty")
            .Must(t => Enum.TryParse<Timeframe>(t, ignoreCase: true, out _))
            .WithMessage("Timeframe must be one of: M1, M5, M15, H1, H4, D1");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class TriggerMLTrainingCommandHandler : IRequestHandler<TriggerMLTrainingCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public TriggerMLTrainingCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(TriggerMLTrainingCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.MLTrainingRun
        {
            Symbol              = request.Symbol.ToUpperInvariant(),
            Timeframe           = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true),
            TriggerType         = Enum.Parse<TriggerType>(request.TriggerType, ignoreCase: true),
            Status              = RunStatus.Queued,
            FromDate            = request.FromDate,
            ToDate              = request.ToDate,
            StartedAt           = DateTime.UtcNow,
            LearnerArchitecture = request.LearnerArchitecture,
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.MLTrainingRun>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "ML training run queued successfully", "00");
    }
}
