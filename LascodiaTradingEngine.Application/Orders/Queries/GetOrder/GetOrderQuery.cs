using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Orders.Queries.DTOs;

namespace LascodiaTradingEngine.Application.Orders.Queries.GetOrder;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetOrderQuery : IRequest<ResponseData<OrderDto>>
{
    [JsonIgnore] public int BusinessId { get; set; }
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetOrderQueryHandler : IRequestHandler<GetOrderQuery, ResponseData<OrderDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetOrderQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }

    public async Task<ResponseData<OrderDto>> Handle(GetOrderQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && x.BusinessId == request.BusinessId, cancellationToken);

        if (entity == null)
            return ResponseData<OrderDto>.Init(null, false, "Order not found", "-14");

        return ResponseData<OrderDto>.Init(_mapper.Map<OrderDto>(entity), true, "Successful", "00");
    }
}
