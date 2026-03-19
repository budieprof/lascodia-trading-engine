using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.CurrencyPairs.Queries.DTOs;

namespace LascodiaTradingEngine.Application.CurrencyPairs.Queries.GetPagedCurrencyPairs;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedCurrencyPairsQuery : PagerRequest<ResponseData<PagedData<CurrencyPairDto>>>
{
    public string? Search   { get; set; }
    public bool?   IsActive { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedCurrencyPairsQueryHandler
    : IRequestHandler<GetPagedCurrencyPairsQuery, ResponseData<PagedData<CurrencyPairDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedCurrencyPairsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<CurrencyPairDto>>> Handle(
        GetPagedCurrencyPairsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);

        var query = _context.GetDbContext()
            .Set<Domain.Entities.CurrencyPair>()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Symbol)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(x => x.Symbol.Contains(request.Search)
                                  || x.BaseCurrency.Contains(request.Search)
                                  || x.QuoteCurrency.Contains(request.Search));

        if (request.IsActive.HasValue)
            query = query.Where(x => x.IsActive == request.IsActive.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<CurrencyPairDto>>(data);

        return ResponseData<PagedData<CurrencyPairDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
