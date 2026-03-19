using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Brokers.Queries.DTOs;

namespace LascodiaTradingEngine.Application.Brokers.Queries.GetBroker;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetBrokerQuery : IRequest<ResponseData<BrokerDto>>
{
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetBrokerQueryHandler : IRequestHandler<GetBrokerQuery, ResponseData<BrokerDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetBrokerQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<BrokerDto>> Handle(GetBrokerQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Broker>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<BrokerDto>.Init(null, false, "Broker not found", "-14");

        return ResponseData<BrokerDto>.Init(_mapper.Map<BrokerDto>(entity), true, "Successful", "00");
    }
}
