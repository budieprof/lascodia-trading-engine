using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.WalkForward.Commands.RunWalkForward;

// ── Command ───────────────────────────────────────────────────────────────────

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
            .NotEmpty().WithMessage("Timeframe cannot be empty");

        RuleFor(x => x.InSampleDays)
            .GreaterThan(0).WithMessage("InSampleDays must be greater than zero");

        RuleFor(x => x.OutOfSampleDays)
            .GreaterThan(0).WithMessage("OutOfSampleDays must be greater than zero");

        RuleFor(x => x.InitialBalance)
            .GreaterThan(0).WithMessage("InitialBalance must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class RunWalkForwardCommandHandler : IRequestHandler<RunWalkForwardCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public RunWalkForwardCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(RunWalkForwardCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.WalkForwardRun
        {
            StrategyId      = request.StrategyId,
            Symbol          = request.Symbol.ToUpperInvariant(),
            Timeframe       = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true),
            FromDate        = request.FromDate,
            ToDate          = request.ToDate,
            InSampleDays    = request.InSampleDays,
            OutOfSampleDays = request.OutOfSampleDays,
            InitialBalance  = request.InitialBalance,
            Status          = RunStatus.Queued,
            StartedAt       = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.WalkForwardRun>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
