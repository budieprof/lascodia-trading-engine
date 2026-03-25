using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.GetPagedMLModels;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedMLModelsQuery : PagerRequestWithFilterType<MLModelQueryFilter, ResponseData<PagedData<MLModelDto>>>
{
}

// ── Filter ────────────────────────────────────────────────────────────────────

public class MLModelQueryFilter
{
    public string? Symbol    { get; set; }
    public string? Timeframe { get; set; }
    public bool?   IsActive  { get; set; }
    public string? Status    { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedMLModelsQueryHandler
    : IRequestHandler<GetPagedMLModelsQuery, ResponseData<PagedData<MLModelDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedMLModelsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<MLModelDto>>> Handle(
        GetPagedMLModelsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<MLModelQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.MLModel>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.TrainedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Symbol))
            query = query.Where(x => x.Symbol == filter.Symbol.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(filter?.Timeframe) && Enum.TryParse<Timeframe>(filter.Timeframe, ignoreCase: true, out var timeframe))
            query = query.Where(x => x.Timeframe == timeframe);

        if (filter?.IsActive.HasValue == true)
            query = query.Where(x => x.IsActive == filter.IsActive!.Value);

        if (!string.IsNullOrWhiteSpace(filter?.Status) && Enum.TryParse<MLModelStatus>(filter.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<MLModelDto>>(data);

        return ResponseData<PagedData<MLModelDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
