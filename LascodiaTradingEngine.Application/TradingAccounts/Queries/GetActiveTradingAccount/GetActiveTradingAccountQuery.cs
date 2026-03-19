using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.DTOs;

namespace LascodiaTradingEngine.Application.TradingAccounts.Queries.GetActiveTradingAccount;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetActiveTradingAccountQuery : IRequest<ResponseData<TradingAccountDto>>
{
    public required long BrokerId { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

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
            .FirstOrDefaultAsync(x => x.BrokerId == request.BrokerId && x.IsActive && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<TradingAccountDto>.Init(null, false, "No active trading account found for broker", "-14");

        return ResponseData<TradingAccountDto>.Init(_mapper.Map<TradingAccountDto>(entity), true, "Successful", "00");
    }
}
