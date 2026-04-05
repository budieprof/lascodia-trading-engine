using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Alerts.Commands.CreateAlert;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Creates a new alert rule that triggers notifications when specified market conditions are met.
/// </summary>
public class CreateAlertCommand : IRequest<ResponseData<long>>
{
    /// <summary>The type of alert (e.g., PriceLevel, DrawdownBreached, SignalGenerated).</summary>
    public required string AlertType     { get; set; }
    /// <summary>The trading symbol this alert monitors (e.g., EURUSD).</summary>
    public required string Symbol        { get; set; }
    /// <summary>The delivery channel for the alert (Email, Webhook, or Telegram).</summary>
    public required string Channel       { get; set; }
    /// <summary>The channel-specific destination (email address, webhook URL, or Telegram chat ID).</summary>
    public required string Destination   { get; set; }
    /// <summary>JSON-encoded condition parameters that define when the alert triggers.</summary>
    public string ConditionJson          { get; set; } = "{}";
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates <see cref="CreateAlertCommand"/> ensuring required fields and valid enum values.</summary>
public class CreateAlertCommandValidator : AbstractValidator<CreateAlertCommand>
{
    public CreateAlertCommandValidator()
    {
        RuleFor(x => x.AlertType)
            .NotEmpty().WithMessage("AlertType is required")
            .Must(t => Enum.TryParse<AlertType>(t, ignoreCase: true, out _))
            .WithMessage("AlertType must be one of: PriceLevel, DrawdownBreached, SignalGenerated, OrderFilled, PositionClosed, MLModelDegraded");

        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol is required")
            .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters");

        RuleFor(x => x.Channel)
            .NotEmpty().WithMessage("Channel is required")
            .Must(c => Enum.TryParse<AlertChannel>(c, ignoreCase: true, out _))
            .WithMessage("Channel must be one of: Email, Webhook, Telegram");

        RuleFor(x => x.Destination)
            .NotEmpty().WithMessage("Destination is required");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Persists a new <see cref="Domain.Entities.Alert"/> entity with the specified type, symbol, channel, and condition.
/// Returns the newly created alert ID.
/// </summary>
public class CreateAlertCommandHandler : IRequestHandler<CreateAlertCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public CreateAlertCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(
        CreateAlertCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.Alert
        {
            AlertType     = Enum.Parse<AlertType>(request.AlertType, ignoreCase: true),
            Symbol        = request.Symbol,
            Channel       = Enum.Parse<AlertChannel>(request.Channel, ignoreCase: true),
            Destination   = request.Destination,
            ConditionJson = request.ConditionJson,
            IsActive      = true
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.Alert>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
