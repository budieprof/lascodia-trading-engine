using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.EconomicEvents.Commands.CreateEconomicEvent;

// ── Command ───────────────────────────────────────────────────────────────────

public class CreateEconomicEventCommand : IRequest<ResponseData<long>>
{
    public required string Title       { get; set; }
    public required string Currency    { get; set; }
    public required string Impact      { get; set; }  // "High" | "Medium" | "Low"
    public DateTime        ScheduledAt { get; set; }
    public string          Source      { get; set; } = "Manual";
    public string?         Forecast    { get; set; }
    public string?         Previous    { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class CreateEconomicEventCommandValidator : AbstractValidator<CreateEconomicEventCommand>
{
    public CreateEconomicEventCommandValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required");

        RuleFor(x => x.Currency)
            .NotEmpty().WithMessage("Currency is required")
            .MaximumLength(3).WithMessage("Currency cannot exceed 3 characters");

        RuleFor(x => x.Impact)
            .NotEmpty().WithMessage("Impact is required")
            .Must(i => Enum.TryParse<EconomicImpact>(i, ignoreCase: true, out _))
            .WithMessage("Impact must be 'High', 'Medium', or 'Low'");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class CreateEconomicEventCommandHandler : IRequestHandler<CreateEconomicEventCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public CreateEconomicEventCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(CreateEconomicEventCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.EconomicEvent
        {
            Title       = request.Title,
            Currency    = request.Currency.ToUpperInvariant(),
            Impact      = Enum.Parse<EconomicImpact>(request.Impact, ignoreCase: true),
            ScheduledAt = request.ScheduledAt,
            Source      = Enum.Parse<EconomicEventSource>(request.Source, ignoreCase: true),
            Forecast    = request.Forecast,
            Previous    = request.Previous
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.EconomicEvent>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
