using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLEvaluation.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLEvaluation.Queries.GetPagedMLShadowEvaluations;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedMLShadowEvaluationsQuery : PagerRequest<ResponseData<PagedData<MLShadowEvaluationDto>>>
{
    public string? Symbol { get; set; }
    public string? Status { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedMLShadowEvaluationsQueryHandler
    : IRequestHandler<GetPagedMLShadowEvaluationsQuery, ResponseData<PagedData<MLShadowEvaluationDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedMLShadowEvaluationsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<MLShadowEvaluationDto>>> Handle(
        GetPagedMLShadowEvaluationsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);

        var query = _context.GetDbContext()
            .Set<Domain.Entities.MLShadowEvaluation>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.StartedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Symbol))
            query = query.Where(x => x.Symbol == request.Symbol.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(request.Status) && Enum.TryParse<ShadowEvaluationStatus>(request.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<MLShadowEvaluationDto>>(data);

        return ResponseData<PagedData<MLShadowEvaluationDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
