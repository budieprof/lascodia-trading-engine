using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLEvaluation.Queries.DTOs;

namespace LascodiaTradingEngine.Application.MLEvaluation.Queries.GetMLShadowEvaluation;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Retrieves a single ML shadow evaluation by its database ID. Returns -14 if not found.
/// </summary>
public class GetMLShadowEvaluationQuery : IRequest<ResponseData<MLShadowEvaluationDto>>
{
    /// <summary>Database ID of the shadow evaluation to retrieve.</summary>
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles single shadow evaluation retrieval by ID, mapping the entity to MLShadowEvaluationDto.
/// </summary>
public class GetMLShadowEvaluationQueryHandler : IRequestHandler<GetMLShadowEvaluationQuery, ResponseData<MLShadowEvaluationDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetMLShadowEvaluationQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<MLShadowEvaluationDto>> Handle(GetMLShadowEvaluationQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.MLShadowEvaluation>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<MLShadowEvaluationDto>.Init(null, false, "Shadow evaluation not found", "-14");

        return ResponseData<MLShadowEvaluationDto>.Init(_mapper.Map<MLShadowEvaluationDto>(entity), true, "Successful", "00");
    }
}
