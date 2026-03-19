using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Sentiment.Queries.DTOs;

namespace LascodiaTradingEngine.Application.Sentiment.Queries.GetPagedCOTReports;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedCOTReportsQuery : PagerRequest<ResponseData<PagedData<COTReportDto>>>
{
    public string? Symbol { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedCOTReportsQueryHandler
    : IRequestHandler<GetPagedCOTReportsQuery, ResponseData<PagedData<COTReportDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedCOTReportsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<COTReportDto>>> Handle(
        GetPagedCOTReportsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);

        var query = _context.GetDbContext()
            .Set<Domain.Entities.COTReport>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.ReportDate)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Symbol))
        {
            string currency = request.Symbol.Length >= 3
                ? request.Symbol[..3].ToUpperInvariant()
                : request.Symbol.ToUpperInvariant();

            query = query.Where(x => x.Currency == currency);
        }

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<COTReportDto>>(data);

        return ResponseData<PagedData<COTReportDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
