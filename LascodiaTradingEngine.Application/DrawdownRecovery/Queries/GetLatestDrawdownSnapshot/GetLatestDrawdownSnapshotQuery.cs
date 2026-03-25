using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.DrawdownRecovery.Queries.DTOs;

namespace LascodiaTradingEngine.Application.DrawdownRecovery.Queries.GetLatestDrawdownSnapshot;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetLatestDrawdownSnapshotQuery : IRequest<ResponseData<DrawdownSnapshotDto>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetLatestDrawdownSnapshotQueryHandler
    : IRequestHandler<GetLatestDrawdownSnapshotQuery, ResponseData<DrawdownSnapshotDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetLatestDrawdownSnapshotQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<DrawdownSnapshotDto>> Handle(
        GetLatestDrawdownSnapshotQuery request, CancellationToken cancellationToken)
    {
        var snapshot = await _context.GetDbContext()
            .Set<Domain.Entities.DrawdownSnapshot>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.RecordedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is null)
            return ResponseData<DrawdownSnapshotDto>.Init(null, false, "No snapshot found", "-14");

        var dto = _mapper.Map<DrawdownSnapshotDto>(snapshot);
        return ResponseData<DrawdownSnapshotDto>.Init(dto, true, "Successful", "00");
    }
}
