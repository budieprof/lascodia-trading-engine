using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.WalkForward.Commands.RunWalkForward;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Queues a walk-forward analysis run that divides the date range into in-sample/out-of-sample
/// windows and evaluates strategy robustness. Executed asynchronously by the <c>WalkForwardWorker</c>.
/// </summary>
public class RunWalkForwardCommand : IRequest<ResponseData<long>>
{
    public long     StrategyId       { get; set; }
    public required string Symbol    { get; set; }
    public required string Timeframe { get; set; }
    public DateTime FromDate         { get; set; }
    public DateTime ToDate           { get; set; }
    public int      InSampleDays     { get; set; } = 90;
    public int      OutOfSampleDays  { get; set; } = 30;
    public decimal  InitialBalance   { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class RunWalkForwardCommandValidator : AbstractValidator<RunWalkForwardCommand>
{
    public RunWalkForwardCommandValidator()
    {
        RuleFor(x => x.StrategyId)
            .GreaterThan(0).WithMessage("StrategyId must be greater than zero");

        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty");

        RuleFor(x => x.Timeframe)
            .NotEmpty().WithMessage("Timeframe cannot be empty")
            .Must(tf => new[] { "M1", "M5", "M15", "H1", "H4", "D1" }.Contains(tf))
            .WithMessage("Timeframe must be one of: M1, M5, M15, H1, H4, D1");

        RuleFor(x => x.ToDate)
            .GreaterThan(x => x.FromDate).WithMessage("ToDate must be after FromDate");

        RuleFor(x => x.InSampleDays)
            .GreaterThan(0).WithMessage("InSampleDays must be greater than zero");

        RuleFor(x => x.OutOfSampleDays)
            .GreaterThan(0).WithMessage("OutOfSampleDays must be greater than zero");

        RuleFor(x => x.InitialBalance)
            .GreaterThan(0).WithMessage("InitialBalance must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Creates a new walk-forward run entity with Queued status for asynchronous processing.</summary>
public class RunWalkForwardCommandHandler : IRequestHandler<RunWalkForwardCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IValidationRunFactory _validationRunFactory;

    public RunWalkForwardCommandHandler(IWriteApplicationDbContext context, IValidationRunFactory validationRunFactory)
    {
        _context = context;
        _validationRunFactory = validationRunFactory;
    }

    public async Task<ResponseData<long>> Handle(RunWalkForwardCommand request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();
        var strategy = await db.Set<Strategy>()
            .FirstOrDefaultAsync(candidate => candidate.Id == request.StrategyId && !candidate.IsDeleted, cancellationToken);

        if (strategy == null)
            return ResponseData<long>.Init(0, false, "Strategy not found", "-14");

        var timeframe = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true);
        string normalizedSymbol = request.Symbol.ToUpperInvariant();
        if (!string.Equals(strategy.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase)
            || strategy.Timeframe != timeframe)
        {
            return ResponseData<long>.Init(
                0,
                false,
                $"Requested symbol/timeframe {normalizedSymbol}/{timeframe} does not match strategy {strategy.Symbol}/{strategy.Timeframe}",
                "-409");
        }

        double totalDays = (request.ToDate - request.FromDate).TotalDays;
        if (request.InSampleDays + request.OutOfSampleDays > totalDays)
        {
            return ResponseData<long>.Init(
                0,
                false,
                $"WalkForward: IS ({request.InSampleDays}d) + OOS ({request.OutOfSampleDays}d) exceeds date range ({totalDays:F0}d)",
                "-11");
        }

        var entity = await _validationRunFactory.BuildWalkForwardRunAsync(
            db,
            new WalkForwardQueueRequest(
                StrategyId: request.StrategyId,
                Symbol: normalizedSymbol,
                Timeframe: timeframe,
                FromDate: request.FromDate,
                ToDate: request.ToDate,
                InSampleDays: request.InSampleDays,
                OutOfSampleDays: request.OutOfSampleDays,
                InitialBalance: request.InitialBalance,
                QueueSource: ValidationRunQueueSources.Manual,
                ReOptimizePerFold: false,
                ParametersSnapshotJson: strategy.ParametersJson),
            cancellationToken);

        await db
            .Set<Domain.Entities.WalkForwardRun>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
