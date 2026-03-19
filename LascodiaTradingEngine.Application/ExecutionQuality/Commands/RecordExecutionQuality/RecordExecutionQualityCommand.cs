using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExecutionQuality.Commands.RecordExecutionQuality;

// ── Command ───────────────────────────────────────────────────────────────────

public class RecordExecutionQualityCommand : IRequest<ResponseData<long>>
{
    public long     OrderId        { get; set; }
    public long?    StrategyId     { get; set; }
    public required string Symbol  { get; set; }
    public required string Session { get; set; }
    public decimal  RequestedPrice { get; set; }
    public decimal  FilledPrice    { get; set; }
    public decimal  SlippagePips   { get; set; }
    public long     SubmitToFillMs { get; set; }
    public bool     WasPartialFill { get; set; }
    public decimal  FillRate       { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class RecordExecutionQualityCommandValidator : AbstractValidator<RecordExecutionQualityCommand>
{
    public RecordExecutionQualityCommandValidator()
    {
        RuleFor(x => x.OrderId)
            .GreaterThan(0).WithMessage("OrderId must be greater than zero");

        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty");

        RuleFor(x => x.Session)
            .NotEmpty().WithMessage("Session cannot be empty")
            .Must(s => Enum.TryParse<TradingSession>(s, ignoreCase: true, out _))
            .WithMessage("Session must be one of: London, NewYork, Asian, LondonNYOverlap");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class RecordExecutionQualityCommandHandler
    : IRequestHandler<RecordExecutionQualityCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public RecordExecutionQualityCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(
        RecordExecutionQualityCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.ExecutionQualityLog
        {
            OrderId        = request.OrderId,
            StrategyId     = request.StrategyId,
            Symbol         = request.Symbol,
            Session        = Enum.Parse<TradingSession>(request.Session, ignoreCase: true),
            RequestedPrice = request.RequestedPrice,
            FilledPrice    = request.FilledPrice,
            SlippagePips   = request.SlippagePips,
            SubmitToFillMs = request.SubmitToFillMs,
            WasPartialFill = request.WasPartialFill,
            FillRate       = request.FillRate,
            RecordedAt     = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.ExecutionQualityLog>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
