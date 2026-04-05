using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
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

    public RunBacktestCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(RunBacktestCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.BacktestRun
        {
            StrategyId     = request.StrategyId,
            Symbol         = request.Symbol,
            Timeframe      = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true),
            FromDate       = request.FromDate,
            ToDate         = request.ToDate,
            InitialBalance = request.InitialBalance,
            Status         = RunStatus.Queued,
            StartedAt      = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.BacktestRun>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Backtest queued successfully", "00");
    }
}
