using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.DTOs;

namespace LascodiaTradingEngine.Application.TradingAccounts.Queries.GetPagedTradingAccounts;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedTradingAccountsQuery : PagerRequest<ResponseData<PagedData<TradingAccountDto>>>
{
    public long? BrokerId { get; set; }
    public bool? IsPaper  { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedTradingAccountsQueryHandler
    : IRequestHandler<GetPagedTradingAccountsQuery, ResponseData<PagedData<TradingAccountDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedTradingAccountsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<TradingAccountDto>>> Handle(
        GetPagedTradingAccountsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);

        var query = _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.IsActive)
            .AsQueryable();

        if (request.BrokerId.HasValue)
            query = query.Where(x => x.BrokerId == request.BrokerId.Value);

        if (request.IsPaper.HasValue)
            query = query.Where(x => x.IsPaper == request.IsPaper.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<TradingAccountDto>>(data);

        return ResponseData<PagedData<TradingAccountDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
