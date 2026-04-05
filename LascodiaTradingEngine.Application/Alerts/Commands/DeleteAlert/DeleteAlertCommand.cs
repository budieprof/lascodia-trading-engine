using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Alerts.Commands.DeleteAlert;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Soft-deletes an existing alert rule by its identifier.
/// </summary>
public class DeleteAlertCommand : IRequest<ResponseData<string>>
{
    /// <summary>The unique identifier of the alert to delete.</summary>
    [JsonIgnore] public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that the alert ID is a positive value.</summary>
public class DeleteAlertCommandValidator : AbstractValidator<DeleteAlertCommand>
{
    public DeleteAlertCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Marks the alert as soft-deleted. Returns not-found if the alert does not exist.
/// </summary>
public class DeleteAlertCommandHandler : IRequestHandler<DeleteAlertCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public DeleteAlertCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(
        DeleteAlertCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Alert>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Alert not found", "-14");

        entity.IsDeleted = true;
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Deleted", true, "Successful", "00");
    }
}
