using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Strategies.Queries.DTOs;

namespace LascodiaTradingEngine.Application.Strategies.Queries.GetStrategy;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetStrategyQuery : IRequest<ResponseData<StrategyDto>>
{
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetStrategyQueryHandler : IRequestHandler<GetStrategyQuery, ResponseData<StrategyDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetStrategyQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<StrategyDto>> Handle(GetStrategyQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<StrategyDto>.Init(null, false, "Strategy not found", "-14");

        return ResponseData<StrategyDto>.Init(_mapper.Map<StrategyDto>(entity), true, "Successful", "00");
    }
}
