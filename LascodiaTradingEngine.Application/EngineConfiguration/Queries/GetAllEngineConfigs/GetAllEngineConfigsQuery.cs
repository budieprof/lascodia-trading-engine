using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.EngineConfiguration.Queries.DTOs;

namespace LascodiaTradingEngine.Application.EngineConfiguration.Queries.GetAllEngineConfigs;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>Retrieves all engine configuration entries ordered alphabetically by key.</summary>
public class GetAllEngineConfigsQuery : IRequest<ResponseData<List<EngineConfigDto>>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Fetches all active engine config entries from the read-only context, sorted by key.</summary>
public class GetAllEngineConfigsQueryHandler : IRequestHandler<GetAllEngineConfigsQuery, ResponseData<List<EngineConfigDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetAllEngineConfigsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<List<EngineConfigDto>>> Handle(
        GetAllEngineConfigsQuery request, CancellationToken cancellationToken)
    {
        var entities = await _context.GetDbContext()
            .Set<Domain.Entities.EngineConfig>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);

        return ResponseData<List<EngineConfigDto>>.Init(
            _mapper.Map<List<EngineConfigDto>>(entities), true, "Successful", "00");
    }
}
