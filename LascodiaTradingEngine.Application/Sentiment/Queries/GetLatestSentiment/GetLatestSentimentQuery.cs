using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Sentiment.Queries.DTOs;

namespace LascodiaTradingEngine.Application.Sentiment.Queries.GetLatestSentiment;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>Retrieves the most recent sentiment snapshot for a given symbol's base currency.</summary>
public class GetLatestSentimentQuery : IRequest<ResponseData<SentimentSnapshotDto>>
{
    /// <summary>The trading symbol; the first 3 characters are used as the currency key.</summary>
    public required string Symbol { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Fetches the latest sentiment snapshot for the derived base currency, ordered by capture date descending.</summary>
public class GetLatestSentimentQueryHandler
    : IRequestHandler<GetLatestSentimentQuery, ResponseData<SentimentSnapshotDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetLatestSentimentQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<SentimentSnapshotDto>> Handle(
        GetLatestSentimentQuery request, CancellationToken cancellationToken)
    {
        // Currency stored as the first 3 chars of symbol or the full symbol
        string currency = request.Symbol.Length >= 3
            ? request.Symbol[..3].ToUpperInvariant()
            : request.Symbol.ToUpperInvariant();

        var snapshot = await _context.GetDbContext()
            .Set<Domain.Entities.SentimentSnapshot>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted && x.Currency == currency)
            .OrderByDescending(x => x.CapturedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is null)
            return ResponseData<SentimentSnapshotDto>.Init(null, false, "No sentiment data found", "-14");

        var dto = _mapper.Map<SentimentSnapshotDto>(snapshot);
        return ResponseData<SentimentSnapshotDto>.Init(dto, true, "Successful", "00");
    }
}
