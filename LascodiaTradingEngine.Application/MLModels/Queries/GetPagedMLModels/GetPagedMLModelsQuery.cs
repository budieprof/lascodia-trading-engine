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

public class GetPagedMLModelsQuery : PagerRequest<ResponseData<PagedData<MLModelDto>>>
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

        var query = _context.GetDbContext()
            .Set<Domain.Entities.MLModel>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.TrainedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Symbol))
            query = query.Where(x => x.Symbol == request.Symbol.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(request.Timeframe) && Enum.TryParse<Timeframe>(request.Timeframe, ignoreCase: true, out var timeframe))
            query = query.Where(x => x.Timeframe == timeframe);

        if (request.IsActive.HasValue)
            query = query.Where(x => x.IsActive == request.IsActive.Value);

        if (!string.IsNullOrWhiteSpace(request.Status) && Enum.TryParse<MLModelStatus>(request.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<MLModelDto>>(data);

        return ResponseData<PagedData<MLModelDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
