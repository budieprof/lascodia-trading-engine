using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyEnsemble.Queries.DTOs;

namespace LascodiaTradingEngine.Application.StrategyEnsemble.Queries.GetStrategyAllocations;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetStrategyAllocationsQuery : IRequest<ResponseData<List<StrategyAllocationDto>>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetStrategyAllocationsQueryHandler
    : IRequestHandler<GetStrategyAllocationsQuery, ResponseData<List<StrategyAllocationDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetStrategyAllocationsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<List<StrategyAllocationDto>>> Handle(
        GetStrategyAllocationsQuery request, CancellationToken cancellationToken)
    {
        var allocations = await _context.GetDbContext()
            .Set<Domain.Entities.StrategyAllocation>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.Weight)
            .ToListAsync(cancellationToken);

        var strategies = await _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .Where(s => !s.IsDeleted)
            .ToListAsync(cancellationToken);

        var strategyMap = strategies.ToDictionary(s => s.Id, s => s.Name);

        var dtos = _mapper.Map<List<StrategyAllocationDto>>(allocations);

        foreach (var dto in dtos)
        {
            if (strategyMap.TryGetValue(dto.StrategyId, out var name))
                dto.StrategyName = name;
        }

        return ResponseData<List<StrategyAllocationDto>>.Init(dtos, true, "Successful", "00");
    }
}
