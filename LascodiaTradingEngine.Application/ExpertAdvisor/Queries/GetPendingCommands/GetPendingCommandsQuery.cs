using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.ExpertAdvisor.Queries.DTOs;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Queries.GetPendingCommands;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Retrieves all unacknowledged commands queued for a specific EA instance.
/// The EA polls this endpoint to discover new work (modify SL/TP, close position, cancel order, etc.).
/// </summary>
public class GetPendingCommandsQuery : IRequest<ResponseData<List<EACommandDto>>>
{
    /// <summary>The EA instance to retrieve pending commands for.</summary>
    public required string EAInstanceId { get; set; }

    /// <summary>Optional filter to only return commands created after this timestamp (for incremental polling).</summary>
    public DateTime? Since { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles pending command retrieval. Queries unacknowledged EACommand records for the target instance,
/// optionally filtered by creation time, ordered oldest-first so the EA processes them in FIFO order.
/// </summary>
public class GetPendingCommandsQueryHandler : IRequestHandler<GetPendingCommandsQuery, ResponseData<List<EACommandDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public GetPendingCommandsQueryHandler(IReadApplicationDbContext context, IMapper mapper, IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _mapper         = mapper;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<List<EACommandDto>>> Handle(GetPendingCommandsQuery request, CancellationToken cancellationToken)
    {
        if (!await _ownershipGuard.IsOwnerAsync(request.EAInstanceId, cancellationToken))
            return ResponseData<List<EACommandDto>>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        var query = _context.GetDbContext()
            .Set<Domain.Entities.EACommand>()
            .AsNoTracking()
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
