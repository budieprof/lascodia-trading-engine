using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Alerts.Commands.CreateAlert;

// ── Command ───────────────────────────────────────────────────────────────────

public class CreateAlertCommand : IRequest<ResponseData<long>>
{
    public required string AlertType     { get; set; }
    public required string Symbol        { get; set; }
    public required string Channel       { get; set; }
    public required string Destination   { get; set; }
    public string ConditionJson          { get; set; } = "{}";
}

// ── Validator ─────────────────────────────────────────────────────────────────

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
