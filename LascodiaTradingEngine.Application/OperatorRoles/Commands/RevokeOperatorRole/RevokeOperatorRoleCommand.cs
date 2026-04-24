using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;

namespace LascodiaTradingEngine.Application.OperatorRoles.Commands.RevokeOperatorRole;

/// <summary>
/// Soft-revokes a single role grant. Idempotent — revoking a role that isn't currently
/// granted returns success. Token-issued grants stay in the JWT until logout / expiry, so
/// revocation only affects future logins. For immediate effect combine with the logout endpoint.
/// </summary>
public class RevokeOperatorRoleCommand : IRequest<ResponseData<string>>
{
    public long   TradingAccountId { get; set; }
    public string Role             { get; set; } = string.Empty;
}

public class RevokeOperatorRoleCommandValidator : AbstractValidator<RevokeOperatorRoleCommand>
{
    public RevokeOperatorRoleCommandValidator()
    {
        RuleFor(x => x.TradingAccountId).GreaterThan(0);
        RuleFor(x => x.Role)
            .NotEmpty()
            .Must(OperatorRoleNames.IsCanonical)
            .WithMessage($"Role must be one of: {string.Join(", ", OperatorRoleNames.AllRoles)}");
    }
}

public class RevokeOperatorRoleCommandHandler
    : IRequestHandler<RevokeOperatorRoleCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public RevokeOperatorRoleCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(
        RevokeOperatorRoleCommand request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();

        var grant = await db.Set<Domain.Entities.OperatorRole>()
            .FirstOrDefaultAsync(x => x.TradingAccountId == request.TradingAccountId
                                   && x.Role == request.Role, cancellationToken);

        if (grant is null)
            return ResponseData<string>.Init(null, true, "Role was not granted", "00");

        grant.IsDeleted = true;
        await db.SaveChangesAsync(cancellationToken);
        return ResponseData<string>.Init("revoked", true, "Role revoked", "00");
    }
}
