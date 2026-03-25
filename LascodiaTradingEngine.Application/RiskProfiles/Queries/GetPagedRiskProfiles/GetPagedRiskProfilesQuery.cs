using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.RiskProfiles.Queries.DTOs;

namespace LascodiaTradingEngine.Application.RiskProfiles.Queries.GetPagedRiskProfiles;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedRiskProfilesQuery : PagerRequestWithFilterType<RiskProfileQueryFilter, ResponseData<PagedData<RiskProfileDto>>>
{
}

public class RiskProfileQueryFilter
{
    public string? Search { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedRiskProfilesQueryHandler
    : IRequestHandler<GetPagedRiskProfilesQuery, ResponseData<PagedData<RiskProfileDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedRiskProfilesQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<RiskProfileDto>>> Handle(
        GetPagedRiskProfilesQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<RiskProfileQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.RiskProfile>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.IsDefault)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Search))
            query = query.Where(x => x.Name.Contains(filter.Search));

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<RiskProfileDto>>(data);

        return ResponseData<PagedData<RiskProfileDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
