using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.ExpertAdvisor.Queries.DTOs;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Queries.GetPendingCommands;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPendingCommandsQuery : IRequest<ResponseData<List<EACommandDto>>>
{
    public required string EAInstanceId { get; set; }
    public DateTime? Since { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPendingCommandsQueryHandler : IRequestHandler<GetPendingCommandsQuery, ResponseData<List<EACommandDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPendingCommandsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<List<EACommandDto>>> Handle(GetPendingCommandsQuery request, CancellationToken cancellationToken)
    {
        var query = _context.GetDbContext()
            .Set<Domain.Entities.EACommand>()
            .Where(x => x.TargetInstanceId == request.EAInstanceId
                      && !x.Acknowledged
                      && !x.IsDeleted);

        if (request.Since.HasValue)
            query = query.Where(x => x.CreatedAt >= request.Since.Value);

        var commands = await query
            .OrderBy(x => x.CreatedAt)
            .ProjectTo<EACommandDto>(_mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken);

        return ResponseData<List<EACommandDto>>.Init(commands, true, "Successful", "00");
    }
}
