using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.EngineConfiguration.Queries.DTOs;

namespace LascodiaTradingEngine.Application.EngineConfiguration.Queries.GetEngineConfig;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetEngineConfigQuery : IRequest<ResponseData<EngineConfigDto>>
{
    public string Key { get; set; } = string.Empty;
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetEngineConfigQueryHandler : IRequestHandler<GetEngineConfigQuery, ResponseData<EngineConfigDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetEngineConfigQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<EngineConfigDto>> Handle(GetEngineConfigQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.EngineConfig>()
            .FirstOrDefaultAsync(x => x.Key == request.Key && !x.IsDeleted, cancellationToken);

        if (entity is null)
            return ResponseData<EngineConfigDto>.Init(null, false, "Engine config not found", "-14");

        return ResponseData<EngineConfigDto>.Init(_mapper.Map<EngineConfigDto>(entity), true, "Successful", "00");
    }
}
