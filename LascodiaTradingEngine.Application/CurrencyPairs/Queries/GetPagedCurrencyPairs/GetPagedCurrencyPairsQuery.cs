using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.CurrencyPairs.Queries.DTOs;

namespace LascodiaTradingEngine.Application.CurrencyPairs.Queries.GetPagedCurrencyPairs;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>Retrieves a paginated list of currency pairs with optional search and active-status filters.</summary>
public class GetPagedCurrencyPairsQuery : PagerRequestWithFilterType<CurrencyPairQueryFilter, ResponseData<PagedData<CurrencyPairDto>>>
{
}

/// <summary>Filter criteria for the paged currency pairs query.</summary>
public class CurrencyPairQueryFilter
{
    /// <summary>Free-text search across symbol, base currency, and quote currency.</summary>
    public string? Search   { get; set; }
    /// <summary>Filter by active/inactive status.</summary>
    public bool?   IsActive { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Queries currency pairs ordered alphabetically by symbol with optional search and active-status filters.</summary>
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
        var filter = request.GetFilter<CurrencyPairQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.CurrencyPair>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Symbol)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Search))
            query = query.Where(x => x.Symbol.Contains(filter.Search)
                                  || x.BaseCurrency.Contains(filter.Search)
                                  || x.QuoteCurrency.Contains(filter.Search));

        if (filter?.IsActive.HasValue == true)
            query = query.Where(x => x.IsActive == filter.IsActive.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<CurrencyPairDto>>(data);

        return ResponseData<PagedData<CurrencyPairDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
