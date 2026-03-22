using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLEvaluation.Commands.RecordPredictionOutcome;

// ── Command ───────────────────────────────────────────────────────────────────

public class RecordPredictionOutcomeCommand : IRequest<ResponseData<string>>
{
    public long    TradeSignalId        { get; set; }
    public required string ActualDirection { get; set; }
    public decimal ActualMagnitudePips  { get; set; }
    public bool    WasProfitable        { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

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
