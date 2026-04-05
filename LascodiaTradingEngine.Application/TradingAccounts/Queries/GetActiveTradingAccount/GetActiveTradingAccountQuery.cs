using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.DTOs;

namespace LascodiaTradingEngine.Application.TradingAccounts.Queries.GetActiveTradingAccount;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>Retrieves the currently active trading account (only one can be active at a time).</summary>
public class GetActiveTradingAccountQuery : IRequest<ResponseData<TradingAccountDto>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Fetches the single active trading account from the read-only context.</summary>
public class GetActiveTradingAccountQueryHandler : IRequestHandler<GetActiveTradingAccountQuery, ResponseData<TradingAccountDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetActiveTradingAccountQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<TradingAccountDto>> Handle(GetActiveTradingAccountQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<TradingAccountDto>.Init(null, false, "No active trading account found", "-14");

        return ResponseData<TradingAccountDto>.Init(_mapper.Map<TradingAccountDto>(entity), true, "Successful", "00");
    }
}
