using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.GetPagedMLTrainingRuns;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedMLTrainingRunsQuery : PagerRequest<ResponseData<PagedData<MLTrainingRunDto>>>
{
    public string? Symbol    { get; set; }
    public string? Timeframe { get; set; }
    public string? Status    { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedMLTrainingRunsQueryHandler
    : IRequestHandler<GetPagedMLTrainingRunsQuery, ResponseData<PagedData<MLTrainingRunDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedMLTrainingRunsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<MLTrainingRunDto>>> Handle(
        GetPagedMLTrainingRunsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);

        var query = _context.GetDbContext()
            .Set<Domain.Entities.MLTrainingRun>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.StartedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Symbol))
            query = query.Where(x => x.Symbol == request.Symbol.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(request.Timeframe) && Enum.TryParse<Timeframe>(request.Timeframe, ignoreCase: true, out var timeframe))
            query = query.Where(x => x.Timeframe == timeframe);

        if (!string.IsNullOrWhiteSpace(request.Status) && Enum.TryParse<RunStatus>(request.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<MLTrainingRunDto>>(data);

        return ResponseData<PagedData<MLTrainingRunDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
