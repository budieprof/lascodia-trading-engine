using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.DTOs;

namespace LascodiaTradingEngine.Application.TradingAccounts.Queries.GetTradingAccount;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetTradingAccountQuery : IRequest<ResponseData<TradingAccountDto>>
{
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetTradingAccountQueryHandler : IRequestHandler<GetTradingAccountQuery, ResponseData<TradingAccountDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetTradingAccountQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<TradingAccountDto>> Handle(GetTradingAccountQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<TradingAccountDto>.Init(null, false, "Trading account not found", "-14");

        return ResponseData<TradingAccountDto>.Init(_mapper.Map<TradingAccountDto>(entity), true, "Successful", "00");
    }
}
