using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyFeedback.Queries.DTOs;

namespace LascodiaTradingEngine.Application.StrategyFeedback.Queries.GetStrategyPerformance;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetStrategyPerformanceQuery : IRequest<ResponseData<StrategyPerformanceSnapshotDto>>
{
    public required long StrategyId { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetStrategyPerformanceQueryHandler
    : IRequestHandler<GetStrategyPerformanceQuery, ResponseData<StrategyPerformanceSnapshotDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetStrategyPerformanceQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<StrategyPerformanceSnapshotDto>> Handle(
        GetStrategyPerformanceQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.StrategyPerformanceSnapshot>()
            .Where(x => x.StrategyId == request.StrategyId && !x.IsDeleted)
            .OrderByDescending(x => x.EvaluatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
            return ResponseData<StrategyPerformanceSnapshotDto>.Init(
                null, false, "No performance snapshot found for this strategy", "-14");

        return ResponseData<StrategyPerformanceSnapshotDto>.Init(
            _mapper.Map<StrategyPerformanceSnapshotDto>(entity), true, "Successful", "00");
    }
}
