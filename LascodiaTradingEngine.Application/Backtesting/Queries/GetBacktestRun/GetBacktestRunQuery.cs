using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Backtesting.Queries.DTOs;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Backtesting.Queries.GetBacktestRun;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetBacktestRunQuery : IRequest<ResponseData<BacktestRunDto>>
{
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetBacktestRunQueryHandler : IRequestHandler<GetBacktestRunQuery, ResponseData<BacktestRunDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetBacktestRunQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<BacktestRunDto>> Handle(
        GetBacktestRunQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.BacktestRun>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<BacktestRunDto>.Init(null, false, "BacktestRun not found", "-14");

        return ResponseData<BacktestRunDto>.Init(
            _mapper.Map<BacktestRunDto>(entity), true, "Successful", "00");
    }
}
