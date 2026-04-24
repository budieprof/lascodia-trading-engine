using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Sentiment.Commands.RecordSentiment;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Records a market sentiment snapshot for a symbol, capturing bullish/bearish/neutral percentages
/// and an overall sentiment score used by the sentiment filter in strategy evaluation.
/// </summary>
public class RecordSentimentCommand : IRequest<ResponseData<long>>
{
    public required string Symbol         { get; set; }
    public required string Source         { get; set; }
    public decimal         SentimentScore { get; set; }
    public decimal         BullishPct     { get; set; }
    public decimal         BearishPct     { get; set; }
    public decimal         NeutralPct     { get; set; }
    public decimal?        Confidence     { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class RecordSentimentCommandValidator : AbstractValidator<RecordSentimentCommand>
{
    public RecordSentimentCommandValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol is required");

        RuleFor(x => x.Source)
            .NotEmpty().WithMessage("Source is required");

        RuleFor(x => x.SentimentScore)
            .InclusiveBetween(-1m, 1m).WithMessage("SentimentScore must be between -1 and 1");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Persists a sentiment snapshot with the raw bullish/bearish/neutral breakdown stored as JSON.</summary>
public class RecordSentimentCommandHandler : IRequestHandler<RecordSentimentCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;

    public RecordSentimentCommandHandler(
        IWriteApplicationDbContext context,
        IIntegrationEventService   eventBus)
    {
        _context  = context;
        _eventBus = eventBus;
    }

    public async Task<ResponseData<long>> Handle(
        RecordSentimentCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.SentimentSnapshot
        {
            Currency       = request.Symbol,
            Source         = Enum.Parse<SentimentSource>(request.Source, ignoreCase: true),
            SentimentScore = request.SentimentScore,
            Confidence     = request.Confidence ?? request.BullishPct,
            RawDataJson    = System.Text.Json.JsonSerializer.Serialize(new
            {
                request.BullishPct,
                request.BearishPct,
                request.NeutralPct
            }),
            CapturedAt = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.SentimentSnapshot>()
            .AddAsync(entity, cancellationToken);

        // Atomically persist the snapshot and publish the integration event.
        // SaveAndPublish writes both through the outbox so a DB failure
        // doesn't produce a phantom event.
        await _eventBus.SaveAndPublish(_context, new SentimentSnapshotCreatedIntegrationEvent
        {
            SnapshotId     = entity.Id,
            Symbol         = entity.Currency ?? string.Empty,
            Source         = entity.Source.ToString(),
            SentimentScore = entity.SentimentScore,
            CapturedAt     = entity.CapturedAt,
        });

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
