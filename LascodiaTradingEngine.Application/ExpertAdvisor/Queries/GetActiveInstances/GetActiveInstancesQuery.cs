using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.ExpertAdvisor.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Queries.GetActiveInstances;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetActiveInstancesQuery : IRequest<ResponseData<List<EAInstanceDto>>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetActiveInstancesQueryHandler : IRequestHandler<GetActiveInstancesQuery, ResponseData<List<EAInstanceDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetActiveInstancesQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<List<EAInstanceDto>>> Handle(GetActiveInstancesQuery request, CancellationToken cancellationToken)
    {
        var instances = await _context.GetDbContext()
            .Set<Domain.Entities.EAInstance>()
            .Where(x => x.Status == EAInstanceStatus.Active && !x.IsDeleted)
            .OrderByDescending(x => x.LastHeartbeat)
            .ProjectTo<EAInstanceDto>(_mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken);

        return ResponseData<List<EAInstanceDto>>.Init(instances, true, "Successful", "00");
    }
}
