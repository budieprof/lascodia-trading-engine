using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Brokers.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Brokers.Queries.GetPagedBrokers;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedBrokersQuery : PagerRequestWithFilterType<BrokerQueryFilter, ResponseData<PagedData<BrokerDto>>>
{
}

public class BrokerQueryFilter
{
    public string? BrokerType { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedBrokersQueryHandler
    : IRequestHandler<GetPagedBrokersQuery, ResponseData<PagedData<BrokerDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedBrokersQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<BrokerDto>>> Handle(
        GetPagedBrokersQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<BrokerQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.Broker>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.IsActive)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.BrokerType) && Enum.TryParse<BrokerType>(filter?.BrokerType, ignoreCase: true, out var brokerType))
            query = query.Where(x => x.BrokerType == brokerType);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<BrokerDto>>(data);

        return ResponseData<PagedData<BrokerDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
