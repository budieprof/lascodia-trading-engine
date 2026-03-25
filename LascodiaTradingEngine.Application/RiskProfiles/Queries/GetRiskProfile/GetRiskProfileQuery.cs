using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.RiskProfiles.Queries.DTOs;

namespace LascodiaTradingEngine.Application.RiskProfiles.Queries.GetRiskProfile;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetRiskProfileQuery : IRequest<ResponseData<RiskProfileDto>>
{
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetRiskProfileQueryHandler : IRequestHandler<GetRiskProfileQuery, ResponseData<RiskProfileDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetRiskProfileQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<RiskProfileDto>> Handle(GetRiskProfileQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.RiskProfile>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<RiskProfileDto>.Init(null, false, "Risk profile not found", "-14");

        return ResponseData<RiskProfileDto>.Init(_mapper.Map<RiskProfileDto>(entity), true, "Successful", "00");
    }
}
