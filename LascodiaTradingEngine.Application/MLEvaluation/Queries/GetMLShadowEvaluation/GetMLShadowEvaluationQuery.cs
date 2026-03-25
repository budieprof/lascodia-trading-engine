using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLEvaluation.Queries.DTOs;

namespace LascodiaTradingEngine.Application.MLEvaluation.Queries.GetMLShadowEvaluation;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetMLShadowEvaluationQuery : IRequest<ResponseData<MLShadowEvaluationDto>>
{
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

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
