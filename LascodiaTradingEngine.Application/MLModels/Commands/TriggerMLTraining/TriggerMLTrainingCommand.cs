using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Commands.TriggerMLTraining;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Queues a new ML training run for a specific symbol and timeframe. Creates an MLTrainingRun
/// record in Queued status, which the MLTrainingWorker picks up and executes asynchronously.
/// </summary>
public class TriggerMLTrainingCommand : IRequest<ResponseData<long>>
{
    /// <summary>Currency pair to train on (e.g. "EURUSD").</summary>
    public required string Symbol    { get; set; }

    /// <summary>Chart timeframe for the training data (e.g. "H1", "D1").</summary>
    public required string Timeframe { get; set; }

    /// <summary>Start date of the training data window.</summary>
    public DateTime        FromDate  { get; set; }

    /// <summary>End date of the training data window.</summary>
    public DateTime        ToDate    { get; set; }

    /// <summary>What triggered this training run (e.g. "Manual", "Scheduled", "Drift"). Defaults to "Manual".</summary>
    public string          TriggerType { get; set; } = "Manual";
    /// <summary>
    /// The trainer architecture to use for this run.
    /// Defaults to <see cref="LearnerArchitecture.BaggedLogistic"/> when not specified.
    /// </summary>
    public LearnerArchitecture LearnerArchitecture { get; set; } = LearnerArchitecture.BaggedLogistic;
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates that Symbol is non-empty (max 10 chars) and Timeframe is a valid enum value.
/// </summary>
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

/// <summary>
/// Handles ML training trigger by creating a new MLTrainingRun in Queued status.
/// The MLTrainingWorker polls for queued runs and executes them asynchronously.
/// Returns the new training run's database ID.
/// </summary>
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
