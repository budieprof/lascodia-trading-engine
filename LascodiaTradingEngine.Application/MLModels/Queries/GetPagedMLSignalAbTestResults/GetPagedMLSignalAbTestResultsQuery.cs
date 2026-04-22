using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.GetPagedMLSignalAbTestResults;

/// <summary>
/// Retrieves terminal signal-level A/B test results with model, market, decision, and time-window filters.
/// </summary>
public class GetPagedMLSignalAbTestResultsQuery
    : PagerRequestWithFilterType<MLSignalAbTestResultQueryFilter, ResponseData<PagedData<MLSignalAbTestResultDto>>>
{
}

/// <summary>
/// Filter criteria for completed signal-level ML A/B tests.
/// </summary>
public class MLSignalAbTestResultQueryFilter
{
    public long? ChampionModelId { get; set; }
    public long? ChallengerModelId { get; set; }
    public string? Symbol { get; set; }
    public string? Timeframe { get; set; }
    public string? Decision { get; set; }
    public DateTime? StartedFromUtc { get; set; }
    public DateTime? StartedToUtc { get; set; }
    public DateTime? CompletedFromUtc { get; set; }
    public DateTime? CompletedToUtc { get; set; }
}

public class GetPagedMLSignalAbTestResultsQueryHandler
    : IRequestHandler<GetPagedMLSignalAbTestResultsQuery, ResponseData<PagedData<MLSignalAbTestResultDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedMLSignalAbTestResultsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }

    public async Task<ResponseData<PagedData<MLSignalAbTestResultDto>>> Handle(
        GetPagedMLSignalAbTestResultsQuery request,
        CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<MLSignalAbTestResultQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.MLSignalAbTestResult>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.CompletedAtUtc)
            .ThenByDescending(x => x.Id)
            .AsQueryable();

        if (filter?.ChampionModelId is > 0)
            query = query.Where(x => x.ChampionModelId == filter.ChampionModelId.Value);

        if (filter?.ChallengerModelId is > 0)
            query = query.Where(x => x.ChallengerModelId == filter.ChallengerModelId.Value);

        if (!string.IsNullOrWhiteSpace(filter?.Symbol))
            query = query.Where(x => x.Symbol == filter.Symbol.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(filter?.Timeframe) &&
            Enum.TryParse<Timeframe>(filter.Timeframe, ignoreCase: true, out var timeframe))
            query = query.Where(x => x.Timeframe == timeframe);

        if (!string.IsNullOrWhiteSpace(filter?.Decision) &&
            Enum.TryParse<AbTestDecision>(filter.Decision, ignoreCase: true, out var decision))
        {
            var decisionName = decision.ToString();
            query = query.Where(x => x.Decision == decisionName);
        }

        if (filter?.StartedFromUtc is not null)
            query = query.Where(x => x.StartedAtUtc >= filter.StartedFromUtc.Value);

        if (filter?.StartedToUtc is not null)
            query = query.Where(x => x.StartedAtUtc <= filter.StartedToUtc.Value);

        if (filter?.CompletedFromUtc is not null)
            query = query.Where(x => x.CompletedAtUtc >= filter.CompletedFromUtc.Value);

        if (filter?.CompletedToUtc is not null)
            query = query.Where(x => x.CompletedAtUtc <= filter.CompletedToUtc.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<MLSignalAbTestResultDto>>(data);

        return ResponseData<PagedData<MLSignalAbTestResultDto>>.Init(
            pager.GetListPagedData(dtos),
            true,
            "Successful",
            "00");
    }
}
