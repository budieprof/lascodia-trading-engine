using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting.Commands.RunBacktest;

// ── Command ───────────────────────────────────────────────────────────────────

public class RunBacktestCommand : IRequest<ResponseData<long>>
{
    public long     StrategyId     { get; set; }
    public required string Symbol  { get; set; }
    public required string Timeframe { get; set; }
    public DateTime FromDate       { get; set; }
    public DateTime ToDate         { get; set; }
    public decimal  InitialBalance { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

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
