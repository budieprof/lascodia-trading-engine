using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.DTOs;

namespace LascodiaTradingEngine.Application.TradingAccounts.Queries.GetTradingAccount;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>Retrieves a single trading account by its unique identifier.</summary>
public class GetTradingAccountQuery : IRequest<ResponseData<TradingAccountDto>>
{
    /// <summary>The unique identifier of the trading account.</summary>
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Fetches a single trading account by ID from the read-only context.</summary>
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
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<TradingAccountDto>.Init(null, false, "Trading account not found", "-14");

        return ResponseData<TradingAccountDto>.Init(_mapper.Map<TradingAccountDto>(entity), true, "Successful", "00");
    }
}
