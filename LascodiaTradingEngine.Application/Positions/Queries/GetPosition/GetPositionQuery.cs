using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Positions.Queries.DTOs;

namespace LascodiaTradingEngine.Application.Positions.Queries.GetPosition;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPositionQuery : IRequest<ResponseData<PositionDto>>
{
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPositionQueryHandler : IRequestHandler<GetPositionQuery, ResponseData<PositionDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPositionQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PositionDto>> Handle(GetPositionQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Position>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<PositionDto>.Init(null, false, "Position not found", "-14");

        return ResponseData<PositionDto>.Init(_mapper.Map<PositionDto>(entity), true, "Successful", "00");
    }
}
