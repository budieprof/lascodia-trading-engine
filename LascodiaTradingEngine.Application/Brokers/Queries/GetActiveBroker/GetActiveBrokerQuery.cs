using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Brokers.Queries.DTOs;

namespace LascodiaTradingEngine.Application.Brokers.Queries.GetActiveBroker;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetActiveBrokerQuery : IRequest<ResponseData<BrokerDto>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetActiveBrokerQueryHandler : IRequestHandler<GetActiveBrokerQuery, ResponseData<BrokerDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetActiveBrokerQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<BrokerDto>> Handle(GetActiveBrokerQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Broker>()
            .FirstOrDefaultAsync(x => x.IsActive && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<BrokerDto>.Init(null, false, "No active broker found", "-14");

        return ResponseData<BrokerDto>.Init(_mapper.Map<BrokerDto>(entity), true, "Successful", "00");
    }
}
