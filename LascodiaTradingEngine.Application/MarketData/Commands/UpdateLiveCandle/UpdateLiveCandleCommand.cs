using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Events;

namespace LascodiaTradingEngine.Application.MarketData.Commands.UpdateLiveCandle;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Updates the in-memory live price cache with a fresh tick and publishes PriceUpdatedIntegrationEvent.
/// Does NOT write to the database — use IngestCandleCommand for persistence.
/// </summary>
public class UpdateLiveCandleCommand : IRequest<ResponseData<string>>
{
    /// <summary>Instrument symbol (e.g. "EURUSD").</summary>
    public required string Symbol    { get; set; }

    /// <summary>Current bid price.</summary>
    public decimal         Bid       { get; set; }

    /// <summary>Current ask price.</summary>
    public decimal         Ask       { get; set; }

    /// <summary>UTC timestamp of the price update.</summary>
    public DateTime        Timestamp { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates that Symbol is non-empty (max 20 chars) and Bid/Ask are positive.
/// </summary>
public class UpdateLiveCandleCommandValidator : AbstractValidator<UpdateLiveCandleCommand>
{
    public UpdateLiveCandleCommandValidator()
    {
        RuleFor(x => x.Symbol).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Bid).GreaterThan(0);
        RuleFor(x => x.Ask).GreaterThan(0);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles live price cache updates. Writes the bid/ask to the ILivePriceCache and publishes
/// a PriceUpdatedIntegrationEvent for downstream consumers (strategy evaluation, risk monitoring, etc.).
/// Does not persist to the database.
/// </summary>
public class UpdateLiveCandleCommandHandler : IRequestHandler<UpdateLiveCandleCommand, ResponseData<string>>
{
    private readonly ILivePriceCache _cache;
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;

    public UpdateLiveCandleCommandHandler(ILivePriceCache cache, IWriteApplicationDbContext context, IIntegrationEventService eventBus)
    {
        _cache    = cache;
        _context  = context;
        _eventBus = eventBus;
    }

    public async Task<ResponseData<string>> Handle(UpdateLiveCandleCommand request, CancellationToken cancellationToken)
    {
        _cache.Update(request.Symbol, request.Bid, request.Ask, request.Timestamp);

        await _eventBus.SaveAndPublish(_context, new PriceUpdatedIntegrationEvent
        {
            Symbol    = request.Symbol,
            Bid       = request.Bid,
            Ask       = request.Ask,
            Timestamp = request.Timestamp
        });

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
