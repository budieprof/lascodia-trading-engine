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
    /// <summary>Instrument symbol (e.g. "EURUSD").</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Best bid price at the time of capture.</summary>
    public decimal BidPrice { get; set; }

    /// <summary>Best ask price at the time of capture.</summary>
    public decimal AskPrice { get; set; }

    /// <summary>Total volume available at the best bid level.</summary>
    public decimal BidVolume { get; set; }

    /// <summary>Total volume available at the best ask level.</summary>
    public decimal AskVolume { get; set; }

    /// <summary>
    /// JSON-serialised depth beyond top-of-book. See <c>OrderBookSnapshot.LevelsJson</c>
    /// for the schema. Null when the broker doesn't expose multi-level DOM.
    /// </summary>
    public string? LevelsJson { get; set; }

    /// <summary>EA instance that captured this order book snapshot.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>Optional idempotency key to prevent duplicate snapshots.</summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>
/// Validates Symbol (non-empty, max 10 chars), positive Bid/Ask prices, non-negative volumes,
/// and non-empty InstanceId (max 100 chars).
/// </summary>
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

/// <summary>
/// Handles order book snapshot persistence. Creates an OrderBookSnapshot record with the top-of-book
/// data, calculated spread, and capture timestamp. Used for liquidity assessment by risk management.
/// </summary>
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
            LevelsJson   = request.LevelsJson,
            InstanceId   = request.InstanceId,
            CapturedAt   = DateTime.UtcNow
        };

        await _context.GetDbContext().Set<OrderBookSnapshot>().AddAsync(entity, cancellationToken);
        await _context.GetDbContext().SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
