using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Alerts.Commands.UpdateAlert;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Partially updates an existing alert rule. Only non-null properties are applied.
/// </summary>
public class UpdateAlertCommand : IRequest<ResponseData<string>>
{
    /// <summary>The unique identifier of the alert to update (set from the route).</summary>
    [JsonIgnore] public long Id { get; set; }

    /// <summary>Updated alert type, if changing.</summary>
    public string?  AlertType     { get; set; }
    /// <summary>Updated symbol, if changing.</summary>
    public string?  Symbol        { get; set; }
    /// <summary>Updated delivery channel, if changing.</summary>
    public string?  Channel       { get; set; }
    /// <summary>Updated destination address, if changing.</summary>
    public string?  Destination   { get; set; }
    /// <summary>Updated condition JSON, if changing.</summary>
    public string?  ConditionJson { get; set; }
    /// <summary>Whether the alert is active; set to false to disable without deleting.</summary>
    public bool?    IsActive      { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates <see cref="UpdateAlertCommand"/> fields when they are provided.</summary>
public class UpdateAlertCommandValidator : AbstractValidator<UpdateAlertCommand>
{
    public UpdateAlertCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Alert Id is required");

        When(x => x.AlertType != null, () =>
            RuleFor(x => x.AlertType)
                .Must(t => Enum.TryParse<AlertType>(t, ignoreCase: true, out _))
                .WithMessage("AlertType must be one of: PriceLevel, DrawdownBreached, SignalGenerated, OrderFilled, PositionClosed"));

        When(x => x.Symbol != null, () =>
            RuleFor(x => x.Symbol)
                .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters"));

        When(x => x.Channel != null, () =>
            RuleFor(x => x.Channel)
                .Must(c => Enum.TryParse<AlertChannel>(c, ignoreCase: true, out _))
                .WithMessage("Channel must be one of: Email, Webhook, Telegram"));
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Applies partial updates to the alert entity. Non-null fields overwrite existing values.
/// </summary>
public class UpdateAlertCommandHandler : IRequestHandler<UpdateAlertCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpdateAlertCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(
        UpdateAlertCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Alert>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Alert not found", "-14");

        if (!string.IsNullOrWhiteSpace(request.AlertType))   entity.AlertType     = Enum.Parse<AlertType>(request.AlertType, ignoreCase: true);
        if (!string.IsNullOrWhiteSpace(request.Symbol))      entity.Symbol        = request.Symbol;
        if (!string.IsNullOrWhiteSpace(request.Channel))     entity.Channel       = Enum.Parse<AlertChannel>(request.Channel, ignoreCase: true);
        if (!string.IsNullOrWhiteSpace(request.Destination)) entity.Destination   = request.Destination;
        if (request.ConditionJson != null)                   entity.ConditionJson = request.ConditionJson;
        if (request.IsActive.HasValue)                       entity.IsActive      = request.IsActive.Value;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Updated", true, "Successful", "00");
    }
}
