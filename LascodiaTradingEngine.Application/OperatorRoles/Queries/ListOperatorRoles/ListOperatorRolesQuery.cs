using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.OperatorRoles.Queries.ListOperatorRoles;

/// <summary>
/// Returns the live role grants for an account, or — when <see cref="TradingAccountId"/>
/// is null — all live grants in the system. Drives the admin UI's role matrix.
/// </summary>
public class ListOperatorRolesQuery : IRequest<ResponseData<List<OperatorRoleDto>>>
{
    /// <summary>Account to scope the query to. <c>null</c> = all accounts.</summary>
    public long? TradingAccountId { get; set; }
}

/// <summary>One row per live <see cref="Domain.Entities.OperatorRole"/> grant.</summary>
public class OperatorRoleDto
{
    public long     Id                  { get; set; }
    public long     TradingAccountId    { get; set; }
    public string   Role                { get; set; } = string.Empty;
    public DateTime AssignedAt          { get; set; }
    public long?    AssignedByAccountId { get; set; }
}

public class ListOperatorRolesQueryHandler
    : IRequestHandler<ListOperatorRolesQuery, ResponseData<List<OperatorRoleDto>>>
{
    private readonly IReadApplicationDbContext _context;

    public ListOperatorRolesQueryHandler(IReadApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<List<OperatorRoleDto>>> Handle(
        ListOperatorRolesQuery request, CancellationToken cancellationToken)
    {
        var query = _context.GetDbContext()
            .Set<Domain.Entities.OperatorRole>()
            .AsNoTracking();

        if (request.TradingAccountId is long accountId)
            query = query.Where(x => x.TradingAccountId == accountId);

        var rows = await query
            .OrderBy(x => x.TradingAccountId).ThenBy(x => x.Role)
            .Select(x => new OperatorRoleDto
            {
                Id                  = x.Id,
                TradingAccountId    = x.TradingAccountId,
                Role                = x.Role,
                AssignedAt          = x.AssignedAt,
                AssignedByAccountId = x.AssignedByAccountId,
            })
            .ToListAsync(cancellationToken);

        return ResponseData<List<OperatorRoleDto>>.Init(rows, true, "Successful", "00");
    }
}
