using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyFeedback.Queries.DTOs;

namespace LascodiaTradingEngine.Application.StrategyFeedback.Queries.GetOptimizationRun;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetOptimizationRunQuery : IRequest<ResponseData<OptimizationRunDto>>
{
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetOptimizationRunQueryHandler : IRequestHandler<GetOptimizationRunQuery, ResponseData<OptimizationRunDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetOptimizationRunQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<OptimizationRunDto>> Handle(GetOptimizationRunQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.OptimizationRun>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity is null)
            return ResponseData<OptimizationRunDto>.Init(null, false, "Optimization run not found", "-14");

        return ResponseData<OptimizationRunDto>.Init(
            _mapper.Map<OptimizationRunDto>(entity), true, "Successful", "00");
    }
}
