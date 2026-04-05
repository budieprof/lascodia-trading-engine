using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Orders.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Queries.GetPagedOrders;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>Returns a paginated list of orders with optional filtering by symbol, status, and order type.</summary>
public class GetPagedOrdersQuery : PagerRequestWithFilterType<OrderQueryFilter, ResponseData<PagedData<OrderDto>>>
{
}

/// <summary>Filter criteria for the paged orders query.</summary>
public class OrderQueryFilter
{
    /// <summary>Free-text search applied to the Symbol field.</summary>
    public string? Search    { get; set; }
    /// <summary>Filter by <see cref="OrderStatus"/> enum name.</summary>
    public string? Status    { get; set; }
    /// <summary>Filter by <see cref="Domain.Enums.OrderType"/> enum name ("Buy" or "Sell").</summary>
    public string? OrderType { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Executes the paged orders query with optional symbol, status, and order-type filters, ordered by creation date descending.</summary>
public class GetPagedOrdersQueryHandler
    : IRequestHandler<GetPagedOrdersQuery, ResponseData<PagedData<OrderDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedOrdersQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<OrderDto>>> Handle(
        GetPagedOrdersQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<OrderQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Search))
            query = query.Where(x => x.Symbol.Contains(filter.Search));

        if (!string.IsNullOrWhiteSpace(filter?.Status) && Enum.TryParse<OrderStatus>(filter.Status, ignoreCase: true, out var statusFilter))
            query = query.Where(x => x.Status == statusFilter);

        if (!string.IsNullOrWhiteSpace(filter?.OrderType) && Enum.TryParse<OrderType>(filter.OrderType, ignoreCase: true, out var orderTypeFilter))
            query = query.Where(x => x.OrderType == orderTypeFilter);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<OrderDto>>(data);

        return ResponseData<PagedData<OrderDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
