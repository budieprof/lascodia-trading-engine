using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Positions.Commands.OpenPosition;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Opens a new position with the specified entry parameters. The position is persisted
/// in Open status and a <see cref="PositionOpenedIntegrationEvent"/> is published.
/// </summary>
public class OpenPositionCommand : IRequest<ResponseData<long>>
{
    /// <summary>Currency pair symbol (e.g. "EURUSD").</summary>
    public required string Symbol            { get; set; }
    /// <summary>Position direction: "Long" or "Short".</summary>
    public required string Direction         { get; set; }  // "Long" | "Short"
    /// <summary>Position size in lots.</summary>
    public decimal         OpenLots          { get; set; }
    /// <summary>Average entry price at which the position was opened.</summary>
    public decimal         AverageEntryPrice { get; set; }
    /// <summary>Optional stop-loss price level.</summary>
    public decimal?        StopLoss          { get; set; }
    /// <summary>Optional take-profit price level.</summary>
    public decimal?        TakeProfit        { get; set; }
    /// <summary>Whether this is a paper-trading (simulated) position.</summary>
    public bool            IsPaper           { get; set; }
    /// <summary>Identifier of the order that opened this position.</summary>
    public long?           OpenOrderId       { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates symbol, direction, lot size, and entry price for opening a new position.</summary>
public class OpenPositionCommandValidator : AbstractValidator<OpenPositionCommand>
{
    public OpenPositionCommandValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty")
            .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters");

        RuleFor(x => x.Direction)
            .NotEmpty().WithMessage("Direction cannot be empty")
            .Must(d => Enum.TryParse<PositionDirection>(d, ignoreCase: true, out _)).WithMessage("Direction must be 'Long' or 'Short'");

        RuleFor(x => x.OpenLots)
            .GreaterThan(0).WithMessage("OpenLots must be greater than zero");

        RuleFor(x => x.AverageEntryPrice)
            .GreaterThan(0).WithMessage("AverageEntryPrice must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Persists a new position in Open status and publishes a <see cref="PositionOpenedIntegrationEvent"/>.</summary>
public class OpenPositionCommandHandler : IRequestHandler<OpenPositionCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;

    public OpenPositionCommandHandler(IWriteApplicationDbContext context, IIntegrationEventService eventBus)
    {
        _context  = context;
        _eventBus = eventBus;
    }

    public async Task<ResponseData<long>> Handle(OpenPositionCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.Position
        {
            Symbol            = request.Symbol,
            Direction         = Enum.Parse<PositionDirection>(request.Direction, ignoreCase: true),
            OpenLots          = request.OpenLots,
            AverageEntryPrice = request.AverageEntryPrice,
            StopLoss          = request.StopLoss,
            TakeProfit        = request.TakeProfit,
            IsPaper           = request.IsPaper,
            OpenOrderId       = request.OpenOrderId,
            Status            = PositionStatus.Open,
            OpenedAt          = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.Position>()
            .AddAsync(entity, cancellationToken);

        await _eventBus.SaveAndPublish(_context, new PositionOpenedIntegrationEvent
        {
            PositionId        = entity.Id,
            OpenOrderId       = entity.OpenOrderId,
            Symbol            = entity.Symbol,
            Direction         = entity.Direction,
            OpenLots          = entity.OpenLots,
            AverageEntryPrice = entity.AverageEntryPrice,
            StopLoss          = entity.StopLoss,
            TakeProfit        = entity.TakeProfit,
            IsPaper           = entity.IsPaper,
            OpenedAt          = entity.OpenedAt,
        });

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
