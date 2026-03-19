using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketData.Queries.DTOs;

namespace LascodiaTradingEngine.Application.MarketData.Queries.GetLivePrice;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetLivePriceQuery : IRequest<ResponseData<LivePriceDto>>
{
    public required string Symbol { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetLivePriceQueryHandler : IRequestHandler<GetLivePriceQuery, ResponseData<LivePriceDto>>
{
    private readonly ILivePriceCache _cache;

    public GetLivePriceQueryHandler(ILivePriceCache cache)
    {
        _cache = cache;
    }

    public Task<ResponseData<LivePriceDto>> Handle(GetLivePriceQuery request, CancellationToken cancellationToken)
    {
        var price = _cache.Get(request.Symbol);

        if (price is null)
            return Task.FromResult(ResponseData<LivePriceDto>.Init(null, false, "No live price available", "-14"));

        var dto = new LivePriceDto
        {
            Symbol    = request.Symbol,
            Bid       = price.Value.Bid,
            Ask       = price.Value.Ask,
            Spread    = price.Value.Ask - price.Value.Bid,
            Timestamp = price.Value.Timestamp
        };

        return Task.FromResult(ResponseData<LivePriceDto>.Init(dto, true, "Successful", "00"));
    }
}
