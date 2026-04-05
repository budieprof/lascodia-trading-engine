using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLEvaluation.Commands.RecordPredictionOutcome;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Records the actual market outcome for a previously predicted trade signal.
/// Updates all MLModelPredictionLog records associated with the signal, setting the actual direction,
/// magnitude, profitability, and whether the prediction was correct. The MLShadowArbiterWorker
/// uses these outcomes to advance shadow evaluations.
/// </summary>
public class RecordPredictionOutcomeCommand : IRequest<ResponseData<string>>
{
    /// <summary>Database ID of the trade signal whose outcome is being recorded.</summary>
    public long    TradeSignalId        { get; set; }

    /// <summary>Actual market direction that occurred: "Buy" or "Sell".</summary>
    public required string ActualDirection { get; set; }

    /// <summary>Actual price movement magnitude in pips.</summary>
    public decimal ActualMagnitudePips  { get; set; }

    /// <summary>Whether the trade would have been profitable based on the actual outcome.</summary>
    public bool    WasProfitable        { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates that TradeSignalId is positive and ActualDirection is a valid TradeDirection enum value.
/// </summary>
public class RecordPredictionOutcomeCommandValidator : AbstractValidator<RecordPredictionOutcomeCommand>
{
    public RecordPredictionOutcomeCommandValidator()
    {
        RuleFor(x => x.TradeSignalId)
            .GreaterThan(0).WithMessage("TradeSignalId must be greater than zero");

        RuleFor(x => x.ActualDirection)
            .NotEmpty().WithMessage("ActualDirection is required")
            .Must(d => Enum.TryParse<TradeDirection>(d, ignoreCase: true, out _)).WithMessage("ActualDirection must be 'Buy' or 'Sell'");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles prediction outcome recording. Loads all MLModelPredictionLog entries for the given
/// trade signal, then updates each with the actual direction, magnitude, profitability, and
/// direction correctness. Returns -14 if no prediction logs exist for the signal.
/// </summary>
public class RecordPredictionOutcomeCommandHandler : IRequestHandler<RecordPredictionOutcomeCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public RecordPredictionOutcomeCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(RecordPredictionOutcomeCommand request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();

        var actualDirection = Enum.Parse<TradeDirection>(request.ActualDirection, ignoreCase: true);
        var now = DateTime.UtcNow;

        // Update prediction logs with outcome data.
        // Shadow evaluation advancement is handled by MLShadowArbiterWorker.
        var logs = await db.Set<Domain.Entities.MLModelPredictionLog>()
            .Where(x => x.TradeSignalId == request.TradeSignalId && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        if (logs.Count == 0)
            return ResponseData<string>.Init(null, false, "No prediction logs found for this trade signal", "-14");

        foreach (var log in logs)
        {
            log.ActualDirection     = actualDirection;
            log.ActualMagnitudePips = request.ActualMagnitudePips;
            log.WasProfitable       = request.WasProfitable;
            log.DirectionCorrect    = log.PredictedDirection == actualDirection;
            log.OutcomeRecordedAt   = now;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Outcome recorded successfully", true, "Successful", "00");
    }
}
