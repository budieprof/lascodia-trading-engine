using System.Text.Json;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Handles the "chicken-and-egg" case where <c>StrategyGenerationWorker</c> proposes a
/// CompositeML candidate for a (Symbol, Timeframe) combo that has no active MLModel.
///
/// Without this path, the candidate's in-sample backtest produces zero trades (the
/// evaluator returns null on every bar for lack of a model), the screening engine
/// rejects it on <see cref="ScreeningFailureReason.ZeroTradesIS"/>, and no strategy
/// is ever created for the combo. But because no strategy exists, <c>MLTrainingWorker</c>
/// never queues a training run for it — so no model is ever produced. Deadlock.
///
/// This service breaks the deadlock by:
/// <list type="number">
///   <item>Parking the candidate as a <c>Strategy</c> row with
///         <c>Status = Paused, LifecycleStage = PendingModel</c>.</item>
///   <item>Queuing an <see cref="MLTrainingRun"/> with
///         <c>TriggerType = AutoGenerationDeferred</c> so <c>MLTrainingWorker</c>
///         will train the missing model.</item>
///   <item>Writing an audit record so operators can see what was deferred and why.</item>
/// </list>
///
/// When the training run completes and <c>MLTrainingWorker</c> activates the new model,
/// it publishes <c>MLModelActivatedIntegrationEvent</c>. A handler subscribed to that
/// event re-runs screening on any parked strategies matching the combo.
/// </summary>
public interface IDeferredCompositeMLRegistrar
{
    /// <summary>
    /// Checks whether an active <see cref="MLModel"/> exists for the given combo. If not,
    /// parks the candidate as a <c>PendingModel</c> strategy and queues a training run.
    /// Returns <c>true</c> when the candidate was deferred (caller should not log it as
    /// a screening failure), <c>false</c> when a model already exists (caller handles
    /// the rejection normally).
    /// </summary>
    Task<bool> TryDeferAsync(
        string symbol,
        Timeframe timeframe,
        string parametersJson,
        string? cycleId,
        string? candidateHash,
        CancellationToken ct);
}

[RegisterService(ServiceLifetime.Singleton, typeof(IDeferredCompositeMLRegistrar))]
internal sealed class DeferredCompositeMLRegistrar : IDeferredCompositeMLRegistrar
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeferredCompositeMLRegistrar> _logger;

    public DeferredCompositeMLRegistrar(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<DeferredCompositeMLRegistrar> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<bool> TryDeferAsync(
        string symbol,
        Timeframe timeframe,
        string parametersJson,
        string? cycleId,
        string? candidateHash,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var readDb = readCtx.GetDbContext();

        // ── Gate 1: does an active model already exist? ───────────────────────
        // If yes, the caller should treat the zero-trade backtest as a genuine
        // rejection (the model exists and still didn't produce trades — that's a
        // signal the strategy doesn't trade on that combo, not a deadlock).
        bool hasActiveModel = await readDb.Set<MLModel>()
            .AsNoTracking()
            .AnyAsync(m => m.Symbol == symbol
                        && m.Timeframe == timeframe
                        && m.IsActive
                        && !m.IsDeleted, ct);

        if (hasActiveModel)
            return false;

        // ── Gate 2: is a PendingModel strategy already parked for this combo? ─
        // The unique index IX_Strategy_ActiveGenerationKey prevents duplicate
        // (CompositeML, symbol, timeframe) rows while non-deleted. We rely on it
        // as a hard guarantee, but also check explicitly to avoid tripping the
        // constraint in a new transaction — happier error path.
        bool alreadyParked = await readDb.Set<Strategy>()
            .AsNoTracking()
            .AnyAsync(s => s.Symbol == symbol
                        && s.Timeframe == timeframe
                        && s.StrategyType == StrategyType.CompositeML
                        && !s.IsDeleted, ct);

        if (alreadyParked)
        {
            _logger.LogDebug(
                "DeferredCompositeMLRegistrar: {Symbol}/{Tf} already parked — skipping",
                symbol, timeframe);
            return true;
        }

        var writeDb = writeCtx.GetDbContext();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // ── Park the candidate ────────────────────────────────────────────────
        var parkedStrategy = new Strategy
        {
            Name           = $"{symbol} CompositeML {timeframe} (pending model)",
            Description    = "Auto-generated CompositeML candidate parked pending MLModel training. " +
                             "Will be re-screened when MLModelActivatedIntegrationEvent fires for this combo.",
            StrategyType   = StrategyType.CompositeML,
            Symbol         = symbol,
            Timeframe      = timeframe,
            ParametersJson = parametersJson,
            Status         = StrategyStatus.Paused,
            PauseReason    = "Awaiting MLModel for CompositeML evaluation",
            LifecycleStage = StrategyLifecycleStage.PendingModel,
            LifecycleStageEnteredAt = now,
            CreatedAt      = now,
            GenerationCycleId = cycleId,
            GenerationCandidateId = candidateHash,
            ValidationPriority = 100,
        };
        writeDb.Set<Strategy>().Add(parkedStrategy);

        // ── Queue a training run ──────────────────────────────────────────────
        // Use a 365-day window by default; MLTrainingWorker will clamp against
        // actual candle availability at pickup time. Priority is encoded via
        // TriggerType = AutoGenerationDeferred which MLTrainingWorker can
        // prioritise over regular scheduled runs if it chooses.
        var trainingRun = new MLTrainingRun
        {
            Symbol       = symbol,
            Timeframe    = timeframe,
            TriggerType  = TriggerType.AutoDeferred,
            Status       = RunStatus.Queued,
            FromDate     = now.AddDays(-365),
            ToDate       = now,
            StartedAt    = now,
            AttemptCount = 0,
            MaxAttempts  = 3,
        };
        writeDb.Set<MLTrainingRun>().Add(trainingRun);

        try
        {
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Most likely path: another cycle's parallel task beat us to the
            // unique-index constraint on (StrategyType, Symbol, Timeframe). Treat
            // as "already parked" — returning true lets the caller skip the
            // normal failure logging path.
            _logger.LogInformation(ex,
                "DeferredCompositeMLRegistrar: {Symbol}/{Tf} concurrent parking conflict — treating as already deferred",
                symbol, timeframe);
            return true;
        }

        // ── Audit trail ───────────────────────────────────────────────────────
        try
        {
            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "Strategy",
                EntityId     = parkedStrategy.Id,
                DecisionType = "StrategyGeneration",
                Outcome      = "Deferred",
                Reason       = $"CompositeML on {symbol}/{timeframe} deferred: no active MLModel. Training run {trainingRun.Id} queued.",
                ContextJson  = JsonSerializer.Serialize(new
                {
                    strategyType = nameof(StrategyType.CompositeML),
                    symbol,
                    timeframe = timeframe.ToString(),
                    strategyId = parkedStrategy.Id,
                    trainingRunId = trainingRun.Id,
                    cycleId,
                    candidateHash,
                }, JsonOpts),
                Source = "StrategyGenerationWorker",
            }, ct);
        }
        catch (Exception ex)
        {
            // Best effort — the strategy and training run are the authoritative
            // records. An audit write failure must not roll them back.
            _logger.LogWarning(ex,
                "DeferredCompositeMLRegistrar: audit write failed for parked strategy {StrategyId}",
                parkedStrategy.Id);
        }

        _logger.LogInformation(
            "DeferredCompositeMLRegistrar: parked CompositeML for {Symbol}/{Tf} (strategy {StrategyId}, training run {RunId})",
            symbol, timeframe, parkedStrategy.Id, trainingRun.Id);

        return true;
    }
}
