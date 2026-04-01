using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveOrderBookSnapshot;

/// <summary>
/// Receives top-of-book order book data from an EA instance for liquidity assessment.
/// </summary>
public class ReceiveOrderBookSnapshotCommand : IRequest<ResponseData<long>>
{
    public string Symbol { get; set; } = string.Empty;
    public decimal BidPrice { get; set; }
    public decimal AskPrice { get; set; }
    public decimal BidVolume { get; set; }
    public decimal AskVolume { get; set; }
    public string InstanceId { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
}

public class ReceiveOrderBookSnapshotCommandValidator : AbstractValidator<ReceiveOrderBookSnapshotCommand>
{
    public ReceiveOrderBookSnapshotCommandValidator()
    {
        RuleFor(x => x.Symbol).NotEmpty().MaximumLength(10);
        RuleFor(x => x.BidPrice).GreaterThan(0);
        RuleFor(x => x.AskPrice).GreaterThan(0);
        RuleFor(x => x.BidVolume).GreaterThanOrEqualTo(0);
        RuleFor(x => x.AskVolume).GreaterThanOrEqualTo(0);
        RuleFor(x => x.InstanceId).NotEmpty().MaximumLength(100);
    }
}

public class ReceiveOrderBookSnapshotCommandHandler : IRequestHandler<ReceiveOrderBookSnapshotCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public ReceiveOrderBookSnapshotCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(
        ReceiveOrderBookSnapshotCommand request,
        CancellationToken cancellationToken)
    {
        var entity = new OrderBookSnapshot
        {
            Symbol       = request.Symbol.ToUpperInvariant(),
            BidPrice     = request.BidPrice,
            AskPrice     = request.AskPrice,
            BidVolume    = request.BidVolume,
            AskVolume    = request.AskVolume,
            SpreadPoints = request.AskPrice - request.BidPrice,
            InstanceId   = request.InstanceId,
            CapturedAt   = DateTime.UtcNow
        };

        await _context.GetDbContext().Set<OrderBookSnapshot>().AddAsync(entity, cancellationToken);
        await _context.GetDbContext().SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
