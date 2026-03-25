using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.ExecutionQuality.Queries.DTOs;

namespace LascodiaTradingEngine.Application.ExecutionQuality.Queries.GetExecutionQualityLog;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetExecutionQualityLogQuery : IRequest<ResponseData<ExecutionQualityLogDto>>
{
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetExecutionQualityLogQueryHandler
    : IRequestHandler<GetExecutionQualityLogQuery, ResponseData<ExecutionQualityLogDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetExecutionQualityLogQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<ExecutionQualityLogDto>> Handle(
        GetExecutionQualityLogQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.ExecutionQualityLog>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<ExecutionQualityLogDto>.Init(null, false, "Execution quality log not found", "-14");

        return ResponseData<ExecutionQualityLogDto>.Init(
            _mapper.Map<ExecutionQualityLogDto>(entity), true, "Successful", "00");
    }
}
