using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Orders.Queries.DTOs;

namespace LascodiaTradingEngine.Application.Orders.Queries.GetPagedOrders;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedOrdersQuery : PagerRequest<ResponseData<PagedData<OrderDto>>>
{
    [JsonIgnore]
    public int BusinessId { get; set; }

    public string? Search { get; set; }
    public string? Status { get; set; }
    public string? OrderType { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedOrdersQueryHandler
    : IRequestHandler<GetPagedOrdersQuery, ResponseData<PagedData<OrderDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedOrdersQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }

    public async Task<ResponseData<PagedData<OrderDto>>> Handle(
        GetPagedOrdersQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);

        var query = _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .Where(x => x.BusinessId == request.BusinessId && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(x => x.Symbol.Contains(request.Search));

        if (!string.IsNullOrWhiteSpace(request.Status))
            query = query.Where(x => x.Status == request.Status);

        if (!string.IsNullOrWhiteSpace(request.OrderType))
            query = query.Where(x => x.OrderType == request.OrderType);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<OrderDto>>(data);

        return ResponseData<PagedData<OrderDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
