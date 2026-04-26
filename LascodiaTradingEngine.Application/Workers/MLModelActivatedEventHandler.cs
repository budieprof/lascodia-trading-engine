using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Writes the governance audit record for <see cref="MLModelActivatedIntegrationEvent"/>.
/// The handler is deliberately state-aware: it verifies the durable <see cref="MLModel"/>
/// activation before logging, uses the write context for idempotency so read-replica lag
/// cannot duplicate audits, and records structured context for post-incident review.
/// </summary>
public sealed class MLModelActivatedEventHandler : IIntegrationEventHandler<MLModelActivatedIntegrationEvent>
{
    private const string EntityType = "MLModel";
    private const string DecisionType = "ModelActivated";
    private const string Outcome = "Active";
    private const string Source = nameof(MLModelActivatedEventHandler);
    private static readonly TimeSpan AuditLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // IServiceScopeFactory is used instead of injecting scoped services directly because
    // this handler is Transient but is invoked from the singleton event-bus consumer loop.
    // Creating an explicit scope per Handle call prevents EF Core DbContext sharing across
    // concurrent event deliveries.
    private readonly IServiceScopeFactory                   _scopeFactory;
    private readonly ILogger<MLModelActivatedEventHandler>  _logger;
    private readonly IDistributedLock?                      _distributedLock;

    /// <summary>
    /// Initialises the handler with the scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a fresh DI scope per event invocation, ensuring scoped services
    /// such as <see cref="IWriteApplicationDbContext"/> and <see cref="IMediator"/> are
    /// properly isolated and disposed after each call.
    /// </param>
    /// <param name="logger">Structured logger for diagnostics and duplicate-skip notices.</param>
    public MLModelActivatedEventHandler(
        IServiceScopeFactory                  scopeFactory,
        ILogger<MLModelActivatedEventHandler> logger,
        IDistributedLock?                     distributedLock = null)
    {
        _scopeFactory    = scopeFactory;
        _logger          = logger;
        _distributedLock = distributedLock;
    }

    /// <summary>
    /// Entry point called by the event bus when an <see cref="MLModelActivatedIntegrationEvent"/>
    /// is received. Logs the activation, performs an idempotency check, and persists the
    /// promotion audit entry.
    /// </summary>
    /// <param name="event">
    /// The integration event published when an ML model becomes production-serving.
    /// Consumed fields:
    /// <list type="bullet">
    ///   <item><see cref="MLModelActivatedIntegrationEvent.NewModelId"/> — primary key of
    ///         the newly active <c>MLModel</c>; used as <c>DecisionLog.EntityId</c> and for
    ///         the idempotency check.</item>
    ///   <item><see cref="MLModelActivatedIntegrationEvent.OldModelId"/> — the superseded
    ///         model's ID, if one existed. Null when this is the first model for the
    ///         symbol/timeframe combination.</item>
    ///   <item><see cref="MLModelActivatedIntegrationEvent.Symbol"/> and
    ///         <see cref="MLModelActivatedIntegrationEvent.Timeframe"/> — identify which
    ///         instrument and granularity the model targets.</item>
    ///   <item><see cref="MLModelActivatedIntegrationEvent.DirectionAccuracy"/> — the
    ///         fraction of direction predictions that were correct on training data (0.0–1.0),
    ///         stored in the audit reason for governance review.</item>
    ///   <item><see cref="MLModelActivatedIntegrationEvent.TrainingRunId"/> — links the
    ///         activated model back to the <c>MLTrainingRun</c> that produced it.</item>
    /// </list>
    /// </param>
    public async Task Handle(MLModelActivatedIntegrationEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (!IsValidEnvelope(@event))
            return;

        await using var activationLock = await TryAcquireActivationLockAsync(@event.NewModelId);

        // Create a fresh async DI scope to safely resolve scoped EF Core services.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var mediator    = scope.ServiceProvider.GetRequiredService<IMediator>();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var writeDb = writeContext.GetDbContext();

        var model = await writeDb
            .Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.Id == @event.NewModelId)
            .Select(m => new ActivatedModelSnapshot(
                m.Id,
                m.Symbol,
                m.Timeframe,
                m.ModelVersion,
                m.Status,
                m.IsActive,
                m.ActivatedAt,
                m.DirectionAccuracy,
                m.TrainingSamples,
                m.LearnerArchitecture,
                m.PreviousChampionModelId))
            .FirstOrDefaultAsync();

        if (model is null)
        {
            _logger.LogWarning(
                "{Handler}: model {ModelId} not found for activation event {EventId}; skipping audit.",
                Source, @event.NewModelId, @event.Id);
            return;
        }

        if (!HasDurableActivation(model))
        {
            _logger.LogWarning(
                "{Handler}: model {ModelId} has status={Status}, isActive={IsActive}, activatedAt={ActivatedAt}; " +
                "event {EventId} is stale or out of order, skipping audit.",
                Source, model.Id, model.Status, model.IsActive, model.ActivatedAt, @event.Id);
            return;
        }

        if (!EventMatchesModel(@event, model))
        {
            _logger.LogWarning(
                "{Handler}: activation event {EventId} for model {ModelId} has {EventSymbol}/{EventTimeframe}, " +
                "but durable model row is {ModelSymbol}/{ModelTimeframe}; skipping corrupt audit.",
                Source, @event.Id, model.Id, @event.Symbol, @event.Timeframe, model.Symbol, model.Timeframe);
            return;
        }

        // Idempotency: query the write context rather than a read replica so a recent audit
        // write is visible to immediately redelivered events.
        bool alreadyLogged = await writeDb
            .Set<DecisionLog>()
            .AnyAsync(d => d.EntityType == EntityType &&
                           d.EntityId == model.Id &&
                           d.DecisionType == DecisionType);
        if (alreadyLogged)
        {
            _logger.LogDebug(
                "{Handler}: decision log already exists for model {ModelId}; skipping duplicate event {EventId}.",
                Source, model.Id, @event.Id);
            return;
        }

        var replacedModelId = @event.OldModelId ?? model.PreviousChampionModelId;
        if (@event.OldModelId.HasValue &&
            model.PreviousChampionModelId.HasValue &&
            @event.OldModelId.Value != model.PreviousChampionModelId.Value)
        {
            _logger.LogWarning(
                "{Handler}: activation event {EventId} says model {ModelId} replaced {EventOldModelId}, " +
                "but durable PreviousChampionModelId is {ModelOldModelId}; audit will preserve both values.",
                Source, @event.Id, model.Id, @event.OldModelId.Value, model.PreviousChampionModelId.Value);
        }

        var auditAccuracy = NormalizeAccuracy(model.DirectionAccuracy ?? @event.DirectionAccuracy);
        var trainingRunLabel = FormatTrainingRun(@event.TrainingRunId);
        string replacedClause = replacedModelId.HasValue
            ? $"; replaced model {replacedModelId.Value}"
            : string.Empty;

        _logger.LogInformation(
            "{Handler}: model {ModelId} activated for {Symbol}/{Timeframe} " +
            "(accuracy={Accuracy:P2}, trainingRun={TrainingRunId}{Replaced})",
            Source, model.Id, model.Symbol, model.Timeframe, auditAccuracy, @event.TrainingRunId, replacedClause);

        await mediator.Send(new LogDecisionCommand
        {
            EntityType   = EntityType,
            EntityId     = model.Id,
            DecisionType = DecisionType,
            Outcome      = Outcome,
            Reason       = $"{model.Symbol}/{model.Timeframe} model promoted from training run " +
                           $"{trainingRunLabel} (DirectionAccuracy={FormatPercent(auditAccuracy)})" +
                           replacedClause,
            ContextJson  = BuildContextJson(@event, model, auditAccuracy),
            Source       = Source
        });
    }

    private async Task<IAsyncDisposable?> TryAcquireActivationLockAsync(long modelId)
    {
        if (_distributedLock is null)
            return null;

        var lockKey = $"ml:model-activation-audit:{modelId}";
        var lockHandle = await _distributedLock.TryAcquireAsync(lockKey, AuditLockTimeout);
        if (lockHandle is not null)
            return lockHandle;

        _logger.LogWarning(
            "{Handler}: could not acquire audit idempotency lock {LockKey} within {TimeoutSeconds}s.",
            Source, lockKey, AuditLockTimeout.TotalSeconds);

        throw new TimeoutException($"Could not acquire {Source} idempotency lock for model {modelId}.");
    }

    private bool IsValidEnvelope(MLModelActivatedIntegrationEvent @event)
    {
        if (@event.NewModelId <= 0)
        {
            _logger.LogWarning(
                "{Handler}: activation event {EventId} has invalid NewModelId={ModelId}; skipping.",
                Source, @event.Id, @event.NewModelId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(@event.Symbol))
        {
            _logger.LogWarning(
                "{Handler}: activation event {EventId} for model {ModelId} has an empty symbol; skipping.",
                Source, @event.Id, @event.NewModelId);
            return false;
        }

        if (@event.TrainingRunId <= 0)
        {
            _logger.LogWarning(
                "{Handler}: activation event {EventId} for model {ModelId} has invalid TrainingRunId={TrainingRunId}; " +
                "continuing with an unknown training-run label.",
                Source, @event.Id, @event.NewModelId, @event.TrainingRunId);
        }

        return true;
    }

    private static bool HasDurableActivation(ActivatedModelSnapshot model)
        => model.ActivatedAt.HasValue &&
           model.Status is MLModelStatus.Active or MLModelStatus.Superseded;

    private static bool EventMatchesModel(
        MLModelActivatedIntegrationEvent @event,
        ActivatedModelSnapshot model)
        => string.Equals(@event.Symbol.Trim(), model.Symbol.Trim(), StringComparison.OrdinalIgnoreCase) &&
           @event.Timeframe == model.Timeframe;

    private static decimal NormalizeAccuracy(decimal accuracy)
        => Math.Clamp(accuracy, 0m, 1m);

    private static string FormatPercent(decimal value)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{value * 100m:0.00}%");

    private static string FormatTrainingRun(long trainingRunId)
        => trainingRunId > 0
            ? trainingRunId.ToString(CultureInfo.InvariantCulture)
            : "unknown";

    private static string BuildContextJson(
        MLModelActivatedIntegrationEvent @event,
        ActivatedModelSnapshot model,
        decimal auditAccuracy)
        => JsonSerializer.Serialize(new
        {
            EventId = @event.Id,
            @event.SequenceNumber,
            EventActivatedAt = @event.ActivatedAt,
            EventDirectionAccuracy = @event.DirectionAccuracy,
            EventOldModelId = @event.OldModelId,
            ModelId = model.Id,
            model.ModelVersion,
            ModelStatus = model.Status,
            model.IsActive,
            ModelActivatedAt = model.ActivatedAt,
            ModelDirectionAccuracy = model.DirectionAccuracy,
            AuditDirectionAccuracy = auditAccuracy,
            model.TrainingSamples,
            model.LearnerArchitecture,
            model.PreviousChampionModelId,
        }, AuditJsonOptions);

    private sealed record ActivatedModelSnapshot(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        string ModelVersion,
        MLModelStatus Status,
        bool IsActive,
        DateTime? ActivatedAt,
        decimal? DirectionAccuracy,
        int TrainingSamples,
        LearnerArchitecture LearnerArchitecture,
        long? PreviousChampionModelId);
}
