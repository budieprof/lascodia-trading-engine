using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketData.Queries.DTOs;

namespace LascodiaTradingEngine.Application.MarketData.Queries.GetLivePrice;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Retrieves the current live bid/ask price for a symbol from the in-memory price cache.
/// Returns -14 if no live price is available (e.g. no EA streaming data for this symbol).
/// </summary>
public class GetLivePriceQuery : IRequest<ResponseData<LivePriceDto>>
{
    /// <summary>Instrument symbol to look up (e.g. "EURUSD").</summary>
    public required string Symbol { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles live price lookup. Reads from the ILivePriceCache (populated by tick ingestion)
/// and constructs a LivePriceDto with bid, ask, calculated spread, and timestamp.
/// </summary>
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
