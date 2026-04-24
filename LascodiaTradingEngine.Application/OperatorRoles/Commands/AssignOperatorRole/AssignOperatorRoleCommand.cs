using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;

namespace LascodiaTradingEngine.Application.OperatorRoles.Commands.AssignOperatorRole;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Grants a single role to a trading account. Idempotent — re-granting a role that's
/// already live for the account succeeds without inserting a second row. If a previous
/// grant for the same pair was soft-deleted, this revives it instead of creating a new row
/// (so the unique index is satisfied without races).
/// </summary>
public class AssignOperatorRoleCommand : IRequest<ResponseData<string>>
{
    /// <summary>Account to grant the role to.</summary>
    public long   TradingAccountId { get; set; }

    /// <summary>Role name. Must be one of <see cref="OperatorRoleNames.AllRoles"/>.</summary>
    public string Role             { get; set; } = string.Empty;
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class AssignOperatorRoleCommandValidator : AbstractValidator<AssignOperatorRoleCommand>
{
    public AssignOperatorRoleCommandValidator()
    {
        RuleFor(x => x.TradingAccountId).GreaterThan(0);
        RuleFor(x => x.Role)
            .NotEmpty()
            .Must(OperatorRoleNames.IsCanonical)
            .WithMessage($"Role must be one of: {string.Join(", ", OperatorRoleNames.AllRoles)}");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class AssignOperatorRoleCommandHandler
    : IRequestHandler<AssignOperatorRoleCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public AssignOperatorRoleCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(
        AssignOperatorRoleCommand request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();

        var live = await db.Set<Domain.Entities.OperatorRole>()
            .FirstOrDefaultAsync(x => x.TradingAccountId == request.TradingAccountId
                                   && x.Role == request.Role, cancellationToken);

        if (live is not null)
            return ResponseData<string>.Init(null, true, "Role already granted", "00");

        // Check soft-deleted — revive rather than insert a duplicate.
        var revoked = await db.Set<Domain.Entities.OperatorRole>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TradingAccountId == request.TradingAccountId
                                   && x.Role == request.Role
                                   && x.IsDeleted, cancellationToken);

        if (revoked is not null)
        {
            revoked.IsDeleted  = false;
            revoked.AssignedAt = DateTime.UtcNow;
        }
        else
        {
            db.Set<Domain.Entities.OperatorRole>().Add(new Domain.Entities.OperatorRole
            {
                TradingAccountId = request.TradingAccountId,
                Role             = request.Role,
                AssignedAt       = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return ResponseData<string>.Init("granted", true, "Role granted", "00");
    }
}
