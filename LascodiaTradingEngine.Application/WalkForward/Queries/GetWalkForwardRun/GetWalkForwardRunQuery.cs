using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.WalkForward.Queries.DTOs;

namespace LascodiaTradingEngine.Application.WalkForward.Queries.GetWalkForwardRun;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetWalkForwardRunQuery : IRequest<ResponseData<WalkForwardRunDto>>
{
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetWalkForwardRunQueryHandler : IRequestHandler<GetWalkForwardRunQuery, ResponseData<WalkForwardRunDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetWalkForwardRunQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<WalkForwardRunDto>> Handle(GetWalkForwardRunQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.WalkForwardRun>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<WalkForwardRunDto>.Init(null, false, "Walk-forward run not found", "-14");

        return ResponseData<WalkForwardRunDto>.Init(_mapper.Map<WalkForwardRunDto>(entity), true, "Successful", "00");
    }
}
