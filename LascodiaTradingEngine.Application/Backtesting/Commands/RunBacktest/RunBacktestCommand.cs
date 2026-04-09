using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting.Commands.RunBacktest;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Queues a backtest run for a strategy over historical candle data. The backtest is executed
/// asynchronously by the <c>BacktestWorker</c>.
/// </summary>
public class RunBacktestCommand : IRequest<ResponseData<long>>
{
    /// <summary>The strategy to backtest.</summary>
    public long     StrategyId     { get; set; }
    /// <summary>The trading symbol to simulate (e.g., EURUSD).</summary>
    public required string Symbol  { get; set; }
    /// <summary>The candle timeframe (M1, M5, M15, H1, H4, D1).</summary>
    public required string Timeframe { get; set; }
    /// <summary>Start date of the historical data window.</summary>
    public DateTime FromDate       { get; set; }
    /// <summary>End date of the historical data window.</summary>
    public DateTime ToDate         { get; set; }
    /// <summary>Starting equity for the simulated account.</summary>
    public decimal  InitialBalance { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates backtest parameters including strategy ID, timeframe, date range, and initial balance.</summary>
public class RunBacktestCommandValidator : AbstractValidator<RunBacktestCommand>
{
    public RunBacktestCommandValidator()
    {
        RuleFor(x => x.StrategyId)
            .GreaterThan(0).WithMessage("StrategyId must be greater than zero");

        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol is required");

        RuleFor(x => x.Timeframe)
            .NotEmpty().WithMessage("Timeframe is required")
            .Must(tf => new[] { "M1", "M5", "M15", "H1", "H4", "D1" }.Contains(tf))
            .WithMessage("Timeframe must be one of: M1, M5, M15, H1, H4, D1");

        RuleFor(x => x.InitialBalance)
            .GreaterThan(0).WithMessage("InitialBalance must be greater than zero");

        RuleFor(x => x.ToDate)
            .GreaterThan(x => x.FromDate).WithMessage("ToDate must be after FromDate");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Creates a new <see cref="Domain.Entities.BacktestRun"/> with Queued status.
/// The <c>BacktestWorker</c> picks it up for asynchronous execution.
/// </summary>
public class RunBacktestCommandHandler : IRequestHandler<RunBacktestCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IValidationRunFactory _validationRunFactory;

    public RunBacktestCommandHandler(IWriteApplicationDbContext context, IValidationRunFactory validationRunFactory)
    {
        _context = context;
        _validationRunFactory = validationRunFactory;
    }

    public async Task<ResponseData<long>> Handle(RunBacktestCommand request, CancellationToken cancellationToken)
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

        var entity = await _validationRunFactory.BuildBacktestRunAsync(
            db,
            new BacktestQueueRequest(
                StrategyId: request.StrategyId,
                Symbol: normalizedSymbol,
                Timeframe: timeframe,
                FromDate: request.FromDate,
                ToDate: request.ToDate,
                InitialBalance: request.InitialBalance,
                QueueSource: ValidationRunQueueSources.Manual,
                ParametersSnapshotJson: strategy.ParametersJson),
            cancellationToken);

        await db
            .Set<Domain.Entities.BacktestRun>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Backtest queued successfully", "00");
    }
}
