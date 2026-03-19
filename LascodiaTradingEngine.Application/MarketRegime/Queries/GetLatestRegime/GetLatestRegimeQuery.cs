using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketRegime.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MarketRegime.Queries.GetLatestRegime;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetLatestRegimeQuery : IRequest<ResponseData<MarketRegimeSnapshotDto>>
{
    public required string Symbol    { get; set; }
    public required string Timeframe { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetLatestRegimeQueryHandler : IRequestHandler<GetLatestRegimeQuery, ResponseData<MarketRegimeSnapshotDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetLatestRegimeQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<MarketRegimeSnapshotDto>> Handle(
        GetLatestRegimeQuery request, CancellationToken cancellationToken)
    {
        var timeframe = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true);

        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.MarketRegimeSnapshot>()
            .Where(x => x.Symbol == request.Symbol && x.Timeframe == timeframe && !x.IsDeleted)
            .OrderByDescending(x => x.DetectedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity == null)
            return ResponseData<MarketRegimeSnapshotDto>.Init(null, false, "No regime snapshot found", "-14");

        return ResponseData<MarketRegimeSnapshotDto>.Init(_mapper.Map<MarketRegimeSnapshotDto>(entity), true, "Successful", "00");
    }
}
