using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.CurrencyPairs.Queries.DTOs;

namespace LascodiaTradingEngine.Application.CurrencyPairs.Queries.GetCurrencyPair;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>Retrieves a single currency pair by its unique identifier.</summary>
public class GetCurrencyPairQuery : IRequest<ResponseData<CurrencyPairDto>>
{
    /// <summary>The unique identifier of the currency pair.</summary>
    public long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Fetches a single currency pair by ID from the read-only context.</summary>
public class GetCurrencyPairQueryHandler : IRequestHandler<GetCurrencyPairQuery, ResponseData<CurrencyPairDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetCurrencyPairQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<CurrencyPairDto>> Handle(GetCurrencyPairQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.CurrencyPair>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity is null)
            return ResponseData<CurrencyPairDto>.Init(null, false, "Currency pair not found", "-14");

        return ResponseData<CurrencyPairDto>.Init(_mapper.Map<CurrencyPairDto>(entity), true, "Successful", "00");
    }
}
