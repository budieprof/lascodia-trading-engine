using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.EventBus.Events;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.DeadLetters.Commands.ReplayDeadLetter;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Replays a dead-lettered integration event by deserializing its payload and re-publishing
/// it onto the event bus. Marks the dead letter as resolved after successful replay.
/// </summary>
public class ReplayDeadLetterCommand : IRequest<ResponseData<bool>>
{
    /// <summary>The unique identifier of the dead letter event to replay.</summary>
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ReplayDeadLetterCommandValidator : AbstractValidator<ReplayDeadLetterCommand>
{
    public ReplayDeadLetterCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Resolves the integration event type by name, deserializes the JSON payload,
/// publishes it to the event bus, and marks the dead letter as resolved.
/// </summary>
public class ReplayDeadLetterCommandHandler
    : IRequestHandler<ReplayDeadLetterCommand, ResponseData<bool>>
{
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly IReadApplicationDbContext _readContext;
    private readonly IEventBus _eventBus;
    private readonly ILogger<ReplayDeadLetterCommandHandler> _logger;

    public ReplayDeadLetterCommandHandler(
        IWriteApplicationDbContext writeContext,
        IReadApplicationDbContext readContext,
        IEventBus eventBus,
        ILogger<ReplayDeadLetterCommandHandler> logger)
    {
        _writeContext = writeContext;
        _readContext  = readContext;
        _eventBus    = eventBus;
        _logger      = logger;
    }

    public async Task<ResponseData<bool>> Handle(
        ReplayDeadLetterCommand request, CancellationToken cancellationToken)
    {
        var entity = await _readContext.GetDbContext()
            .Set<DeadLetterEvent>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity is null)
            return ResponseData<bool>.Init(false, false, "Dead letter event not found", "-14");

        if (entity.IsResolved)
            return ResponseData<bool>.Init(false, false, "Event is already resolved", "-11");

        // Resolve the integration event type from the EventType name
        var eventType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
            .FirstOrDefault(t => t.Name == entity.EventType && t.IsAssignableTo(typeof(IntegrationEvent)));

        if (eventType is null)
        {
            _logger.LogError(
                "ReplayDeadLetter: cannot resolve event type '{EventType}' — event cannot be replayed",
                entity.EventType);
            return ResponseData<bool>.Init(false, false,
                $"Unknown event type: {entity.EventType}", "-11");
        }

        // Deserialize and re-publish the event onto the event bus
        var @event = JsonSerializer.Deserialize(entity.EventPayload, eventType) as IntegrationEvent;
        if (@event is null)
        {
            _logger.LogError(
                "ReplayDeadLetter: failed to deserialize payload for dead letter {Id}", entity.Id);
            return ResponseData<bool>.Init(false, false,
                "Failed to deserialize event payload", "-11");
        }

        _eventBus.Publish(@event);

        // Mark as resolved after successful replay
        var writeEntity = await _writeContext.GetDbContext()
            .Set<DeadLetterEvent>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (writeEntity is not null)
        {
            writeEntity.IsResolved = true;
            await _writeContext.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            "ReplayDeadLetter: replayed dead letter {Id} (handler={Handler}, eventType={EventType})",
            entity.Id, entity.HandlerName, entity.EventType);

        return ResponseData<bool>.Init(true, true, "Event replayed successfully", "00");
    }
}
