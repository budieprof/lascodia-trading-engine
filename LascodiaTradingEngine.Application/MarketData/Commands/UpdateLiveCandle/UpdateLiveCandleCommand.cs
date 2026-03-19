using MediatR;
using Lascodia.Trading.Engine.EventBus.Abstractions;
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
    public required string Symbol    { get; set; }
    public decimal         Bid       { get; set; }
    public decimal         Ask       { get; set; }
    public DateTime        Timestamp { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class UpdateLiveCandleCommandHandler : IRequestHandler<UpdateLiveCandleCommand, ResponseData<string>>
{
    private readonly ILivePriceCache _cache;
    private readonly IEventBus _eventBus;

    public UpdateLiveCandleCommandHandler(ILivePriceCache cache, IEventBus eventBus)
    {
        _cache    = cache;
        _eventBus = eventBus;
    }

    public Task<ResponseData<string>> Handle(UpdateLiveCandleCommand request, CancellationToken cancellationToken)
    {
        _cache.Update(request.Symbol, request.Bid, request.Ask, request.Timestamp);

        _eventBus.Publish(new PriceUpdatedIntegrationEvent
        {
            Symbol    = request.Symbol,
            Bid       = request.Bid,
            Ask       = request.Ask,
            Timestamp = request.Timestamp
        });

        return Task.FromResult(ResponseData<string>.Init(null, true, "Successful", "00"));
    }
}
