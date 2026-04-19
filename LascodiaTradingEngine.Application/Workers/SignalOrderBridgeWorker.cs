using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ApproveTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.RejectTradeSignal;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Performs Tier 1 (signal-level) validation on newly created trade signals.
/// Subscribes to <see cref="TradeSignalCreatedIntegrationEvent"/> and runs the
/// <see cref="ISignalValidator"/> to approve or reject signals based on their
/// intrinsic quality — without referencing any trading account's state.
/// <para>
/// Approved signals are consumed by trading accounts via the
/// <c>POST /order/from-signal</c> endpoint, which runs Tier 2 (account-level) risk checks.
/// </para>
/// </summary>
public class SignalOrderBridgeWorker : BackgroundService, IIntegrationEventHandler<TradeSignalCreatedIntegrationEvent>
{
    private const int MaxRetries = 3;
    private const int MaxConcurrency = 16;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<SignalOrderBridgeWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _eventBus;
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Caps the number of signals processed concurrently to protect the DB connection pool
    /// under burst load (e.g. strategy evaluates 50 symbols simultaneously).
    /// </summary>
    private readonly SemaphoreSlim _concurrencyLimiter = new(MaxConcurrency, MaxConcurrency);

    /// <summary>
    /// Tracks signal IDs currently being processed to prevent duplicate concurrent work
    /// when the event bus delivers the same event more than once (at-least-once delivery).
    /// </summary>
    private readonly ConcurrentDictionary<long, byte> _inFlight = new();

    /// <summary>
    /// Cancelled when the host begins shutting down. Initialised in the constructor so that
    /// <see cref="Handle"/> can reference it immediately — no race with <see cref="ExecuteAsync"/>.
    /// </summary>
    private readonly CancellationTokenSource _shutdownCts = new();

    public SignalOrderBridgeWorker(
        ILogger<SignalOrderBridgeWorker> logger,
        IServiceScopeFactory scopeFactory,
        IEventBus eventBus,
        TradingMetrics metrics,
        TimeProvider timeProvider)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _eventBus     = eventBus;
        _metrics      = metrics;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stoppingToken.Register(() => _shutdownCts.Cancel());

        _eventBus.Subscribe<TradeSignalCreatedIntegrationEvent, SignalOrderBridgeWorker>();

        stoppingToken.Register(() =>
            _eventBus.Unsubscribe<TradeSignalCreatedIntegrationEvent, SignalOrderBridgeWorker>());

        // Keep the worker alive until shutdown is requested, then drain in-flight signals.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }

        // Drain: wait for all in-flight signals to finish (up to host's ShutdownTimeout).
        // Acquire all semaphore slots — once acquired, all handlers have completed.
        _logger.LogInformation(
            "SignalOrderBridgeWorker: shutdown requested — draining {Count} in-flight signal(s)",
            _inFlight.Count);

        for (int i = 0; i < MaxConcurrency; i++)
        {
            if (!await _concurrencyLimiter.WaitAsync(TimeSpan.FromSeconds(1)))
            {
                _logger.LogWarning(
                    "SignalOrderBridgeWorker: drain timeout — {Count} signal(s) still in-flight",
                    _inFlight.Count);
                break;
            }
        }
    }

    public async Task Handle(TradeSignalCreatedIntegrationEvent @event)
    {
        // Dedup latency clock starts the instant we enter the handler so the
        // histogram captures both in-process and cross-instance cost on the hot
        // path — this is what an SLO alert would fire on if the DB-backed
        // tracker starts thrashing under load.
        var dedupSw = System.Diagnostics.Stopwatch.StartNew();

        // ── In-process deduplication ──────────────────────────────────────────
        if (!_inFlight.TryAdd(@event.TradeSignalId, 0))
        {
            dedupSw.Stop();
            _metrics.SignalDedupDuplicates.Add(1, new KeyValuePair<string, object?>("layer", "in_process"));
            _metrics.SignalDedupLatencyMs.Record(dedupSw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", "duplicate"),
                new KeyValuePair<string, object?>("layer", "in_process"));
            _logger.LogDebug(
                "SignalOrderBridgeWorker: signal {Id} is already being processed — skipping duplicate delivery",
                @event.TradeSignalId);
            return;
        }

        // ── Cross-instance deduplication ─────────────────────────────────────
        // In multi-instance deployments, the same event can be delivered to multiple
        // engine instances. The processed event tracker uses a DB-level atomic check
        // to ensure only one instance handles each event.
        try
        {
            using var dedupScope = _scopeFactory.CreateScope();
            var tracker = dedupScope.ServiceProvider.GetRequiredService<IProcessedEventTracker>();
            if (!await tracker.TryMarkAsProcessedAsync(@event.Id, nameof(SignalOrderBridgeWorker), _shutdownCts.Token))
            {
                dedupSw.Stop();
                _inFlight.TryRemove(@event.TradeSignalId, out _);
                _metrics.SignalDedupDuplicates.Add(1, new KeyValuePair<string, object?>("layer", "cross_instance"));
                _metrics.SignalDedupLatencyMs.Record(dedupSw.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("outcome", "duplicate"),
                    new KeyValuePair<string, object?>("layer", "cross_instance"));
                return;
            }
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            _inFlight.TryRemove(@event.TradeSignalId, out _);
            return;
        }

        // Signal survived both dedup layers; record the accepted-path latency.
        dedupSw.Stop();
        _metrics.SignalDedupLatencyMs.Record(dedupSw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("outcome", "accepted"),
            new KeyValuePair<string, object?>("layer", "cross_instance"));

        try
        {
            await _concurrencyLimiter.WaitAsync(_shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            _inFlight.TryRemove(@event.TradeSignalId, out _);
            _logger.LogInformation(
                "SignalOrderBridgeWorker: host shutting down — dropping signal {Id} before processing",
                @event.TradeSignalId);
            return;
        }

        try
        {
            await HandleWithRetryAsync(@event);
        }
        finally
        {
            _concurrencyLimiter.Release();
            _inFlight.TryRemove(@event.TradeSignalId, out _);
        }
    }

    private async Task HandleWithRetryAsync(TradeSignalCreatedIntegrationEvent @event)
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var attemptCts = new CancellationTokenSource(AttemptTimeout);
            var ct = attemptCts.Token;

            try
            {
                await ProcessSignalAsync(@event, ct);
                return;
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
            {
                _logger.LogError(
                    "SignalOrderBridgeWorker: timeout processing signal {Id} on attempt {Attempt}/{Max} after {Timeout}s",
                    @event.TradeSignalId, attempt, MaxRetries, AttemptTimeout.TotalSeconds);

                if (attempt >= MaxRetries)
                {
                    _metrics.WorkerErrors.Add(1,
                        new KeyValuePair<string, object?>("worker", "SignalOrderBridge"),
                        new KeyValuePair<string, object?>("reason", "timeout"));
                    await DeadLetterSignalAsync(@event.TradeSignalId, $"Timed out after {MaxRetries} attempts ({AttemptTimeout.TotalSeconds}s each)");
                    return;
                }

                // Brief pause before retrying after a timeout
                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 100)));
            }
            catch (Exception ex) when (attempt < MaxRetries && IsTransient(ex))
            {
                _logger.LogWarning(ex,
                    "SignalOrderBridgeWorker: transient error processing signal {Id} (attempt {Attempt}/{Max}) — retrying",
                    @event.TradeSignalId, attempt, MaxRetries);

                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 100)));
            }
            catch (Exception ex)
            {
                // Permanent error or final retry exhausted — do not retry
                _logger.LogError(ex,
                    "SignalOrderBridgeWorker: {ErrorType} error processing signal {Id} on attempt {Attempt}/{Max} — dead-lettering",
                    attempt >= MaxRetries ? "final retry" : "permanent",
                    @event.TradeSignalId, attempt, MaxRetries);

                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "SignalOrderBridge"),
                    new KeyValuePair<string, object?>("reason", attempt >= MaxRetries ? "retries_exhausted" : "permanent_error"));

                await DeadLetterSignalAsync(@event.TradeSignalId,
                    $"{(attempt >= MaxRetries ? "Retries exhausted" : "Permanent error")}: {ex.GetType().Name}: {ex.Message}");
                return;
            }
        }
    }

    private async Task ProcessSignalAsync(TradeSignalCreatedIntegrationEvent @event, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await ProcessSignalCoreAsync(@event, ct);
        }
        finally
        {
            sw.Stop();
            _metrics.WorkerCycleDurationMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("worker", "SignalOrderBridge"));
        }
    }

    private async Task ProcessSignalCoreAsync(TradeSignalCreatedIntegrationEvent @event, CancellationToken ct)
    {
        // Structured logging scope — CorrelationId appears in every log line within this scope
        using var correlationScope = _logger.BeginScope(
            new Dictionary<string, object> { ["CorrelationId"] = @event.CorrelationId });

        using var scope       = _scopeFactory.CreateScope();
        var readContext       = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator          = scope.ServiceProvider.GetRequiredService<IMediator>();
        var signalValidator   = scope.ServiceProvider.GetRequiredService<ISignalValidator>();

        var db = readContext.GetDbContext();

        // Load signal with strategy + risk profile
        var signal = await db.Set<Domain.Entities.TradeSignal>()
            .Include(x => x.Strategy)
                .ThenInclude(s => s.RiskProfile)
            .FirstOrDefaultAsync(x => x.Id == @event.TradeSignalId && !x.IsDeleted, ct);

        if (signal is null)
        {
            _logger.LogWarning("SignalOrderBridgeWorker: signal {Id} not found", @event.TradeSignalId);
            return;
        }

        if (signal.Status != TradeSignalStatus.Pending)
        {
            _logger.LogInformation(
                "SignalOrderBridgeWorker: signal {Id} is no longer Pending (status={Status}) — skipping",
                signal.Id, signal.Status);
            return;
        }

        // Expiry is checked by SignalValidator in Tier 1 validation below.
        // Capturing the evaluation time once here avoids race conditions where
        // the signal expires between multiple checks.
        var evaluationTime = _timeProvider.GetUtcNow().UtcDateTime;

        // ── Guard: strategy must exist and be active ──────────────────────────
        if (signal.Strategy is null)
        {
            _logger.LogError("SignalOrderBridgeWorker: signal {Id} has no associated strategy — rejecting", signal.Id);

            _metrics.SignalsRejected.Add(1, new KeyValuePair<string, object?>("reason", "strategy_missing"));
            await RejectSignalAsync(mediator, signal.Id, "Associated strategy not found (possibly deleted)", ct);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "TradeSignal",
                EntityId     = signal.Id,
                DecisionType = "SignalValidation",
                Outcome      = "Rejected",
                Reason       = "Associated strategy not found (possibly deleted)",
                Source       = "SignalOrderBridgeWorker"
            }, ct);

            return;
        }

        if (signal.Strategy.Status != StrategyStatus.Active)
        {
            _logger.LogInformation(
                "SignalOrderBridgeWorker: signal {Id} strategy {StrategyId} is {Status} — rejecting",
                signal.Id, signal.Strategy.Id, signal.Strategy.Status);

            _metrics.SignalsRejected.Add(1, new KeyValuePair<string, object?>("reason", "strategy_inactive"));
            await RejectSignalAsync(mediator, signal.Id,
                $"Strategy {signal.Strategy.Id} is {signal.Strategy.Status} — only Active strategies can produce valid signals", ct);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "TradeSignal",
                EntityId     = signal.Id,
                DecisionType = "SignalValidation",
                Outcome      = "Rejected",
                Reason       = $"Strategy status is {signal.Strategy.Status}",
                Source       = "SignalOrderBridgeWorker"
            }, ct);

            return;
        }

        // ── Guard: EA data availability for this symbol ───────────────────────
        bool hasActiveEA = await db.Set<Domain.Entities.EAInstance>()
            .ActiveForSymbol(signal.Symbol)
            .AnyAsync(ct);

        if (!hasActiveEA)
        {
            _logger.LogWarning(
                "SignalOrderBridgeWorker: no active EA instance covers symbol {Symbol} — rejecting signal {Id}",
                signal.Symbol, signal.Id);

            _metrics.SignalsRejected.Add(1,
                new KeyValuePair<string, object?>("reason", "data_unavailable"),
                new KeyValuePair<string, object?>("symbol", signal.Symbol));

            await RejectSignalAsync(mediator, signal.Id,
                $"No active EA instance is streaming data for {signal.Symbol} — DATA_UNAVAILABLE", ct);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "TradeSignal",
                EntityId     = signal.Id,
                DecisionType = "SignalValidation",
                Outcome      = "Rejected",
                Reason       = $"No active EA instance covers {signal.Symbol}",
                Source       = "SignalOrderBridgeWorker"
            }, ct);

            return;
        }

        // Resolve risk profile: strategy-level first, then system default
        var riskProfile = signal.Strategy.RiskProfile
            ?? await db.Set<Domain.Entities.RiskProfile>()
                       .FirstOrDefaultAsync(x => x.IsDefault && !x.IsDeleted, ct);

        if (riskProfile is null)
        {
            _logger.LogError("SignalOrderBridgeWorker: no risk profile for signal {Id} — rejecting", signal.Id);

            _metrics.SignalsRejected.Add(1, new KeyValuePair<string, object?>("reason", "no_risk_profile"));
            await RejectSignalAsync(mediator, signal.Id, "No risk profile configured", ct);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "TradeSignal",
                EntityId     = signal.Id,
                DecisionType = "SignalValidation",
                Outcome      = "Rejected",
                Reason       = "No risk profile configured (strategy has none and no system default exists)",
                Source       = "SignalOrderBridgeWorker"
            }, ct);

            return;
        }

        // Load symbol spec for pip size calculations
        var symbolSpec = await db.Set<Domain.Entities.CurrencyPair>()
            .FirstOrDefaultAsync(c => c.Symbol == signal.Symbol && !c.IsDeleted, ct);

        // ── News blackout filter ──────────────────────────────────────────
        var newsFilter = scope.ServiceProvider.GetRequiredService<INewsFilter>();
        var evalOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Common.Options.StrategyEvaluatorOptions>>().Value;
        bool safeToTrade = await newsFilter.IsSafeToTradeAsync(
            signal.Symbol, evaluationTime,
            evalOptions.NewsBlackoutMinutesBefore,
            evalOptions.NewsBlackoutMinutesAfter, ct);

        if (!safeToTrade)
        {
            _logger.LogInformation(
                "SignalOrderBridgeWorker: signal {Id} blocked by news blackout for {Symbol}",
                signal.Id, signal.Symbol);

            _metrics.SignalsRejected.Add(1,
                new KeyValuePair<string, object?>("reason", "news_blackout"),
                new KeyValuePair<string, object?>("symbol", signal.Symbol));

            await RejectSignalAsync(mediator, signal.Id,
                $"High-impact news event within blackout window for {signal.Symbol}", ct);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "TradeSignal",
                EntityId     = signal.Id,
                DecisionType = "SignalValidation",
                Outcome      = "Rejected",
                Reason       = $"News blackout for {signal.Symbol}",
                Source       = "SignalOrderBridgeWorker"
            }, ct);

            return;
        }

        // ── Tier 1: Signal-level validation ──────────────────────────────
        var validationContext = new SignalValidationContext
        {
            Profile    = riskProfile,
            SymbolSpec = symbolSpec,
        };

        var result = await signalValidator.ValidateAsync(signal, validationContext, ct);

        if (!result.Passed)
        {
            _logger.LogInformation(
                "SignalOrderBridgeWorker: signal {Id} failed Tier 1 validation — {Reason}",
                signal.Id, result.BlockReason);

            _metrics.SignalsRejected.Add(1,
                new KeyValuePair<string, object?>("reason", "signal_validation"),
                new KeyValuePair<string, object?>("symbol", signal.Symbol),
                new KeyValuePair<string, object?>("strategy_id", signal.StrategyId.ToString()));

            await RejectSignalAsync(mediator, signal.Id, result.BlockReason!, ct);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "TradeSignal",
                EntityId     = signal.Id,
                DecisionType = "SignalValidation",
                Outcome      = "Rejected",
                Reason       = result.BlockReason!,
                Source       = "SignalOrderBridgeWorker"
            }, ct);

            return;
        }

        // ── Approve signal ───────────────────────────────────────────────
        var approveResult = await mediator.Send(new ApproveTradeSignalCommand { Id = signal.Id }, ct);

        if (!approveResult.status)
        {
            _logger.LogWarning(
                "SignalOrderBridgeWorker: failed to approve signal {Id} — {Message} (likely already processed)",
                signal.Id, approveResult.message);
            return;
        }

        _metrics.SignalsAccepted.Add(1,
            new KeyValuePair<string, object?>("symbol", signal.Symbol),
            new KeyValuePair<string, object?>("strategy_id", signal.StrategyId.ToString()));

        _logger.LogInformation(
            "SignalOrderBridgeWorker: signal {Id} approved ({Direction} {Symbol} lot={Lot}, confidence={Confidence:P0})",
            signal.Id, signal.Direction, signal.Symbol, signal.SuggestedLotSize, signal.Confidence);

        await mediator.Send(new LogDecisionCommand
        {
            EntityType   = "TradeSignal",
            EntityId     = signal.Id,
            DecisionType = "SignalValidation",
            Outcome      = "Approved",
            Reason       = $"{signal.Direction} {signal.Symbol} at {signal.EntryPrice}, lot={signal.SuggestedLotSize}, confidence={signal.Confidence:P0}",
            Source       = "SignalOrderBridgeWorker"
        }, ct);
    }

    /// <summary>
    /// Rejects the signal and logs an audit trail entry so that dead-lettered signals
    /// are queryable by operations (DecisionType=SignalValidation, Outcome=DeadLettered).
    /// If the rejection itself fails, dispatches a critical alert to ensure operators are notified.
    /// </summary>
    private async Task DeadLetterSignalAsync(long signalId, string reason)
    {
        const int maxDlRetries = 2;

        for (int dlAttempt = 1; dlAttempt <= maxDlRetries; dlAttempt++)
        {
            try
            {
                using var scope    = _scopeFactory.CreateScope();
                var mediator       = scope.ServiceProvider.GetRequiredService<IMediator>();

                await RejectSignalAsync(mediator, signalId, $"[DEAD-LETTERED] {reason}", CancellationToken.None);

                await mediator.Send(new LogDecisionCommand
                {
                    EntityType   = "TradeSignal",
                    EntityId     = signalId,
                    DecisionType = "SignalValidation",
                    Outcome      = "DeadLettered",
                    Reason       = reason,
                    Source       = "SignalOrderBridgeWorker"
                });

                return; // success
            }
            catch (Exception ex) when (dlAttempt < maxDlRetries)
            {
                _logger.LogWarning(ex,
                    "SignalOrderBridgeWorker: dead-letter attempt {Attempt}/{Max} failed for signal {Id} — retrying",
                    dlAttempt, maxDlRetries, signalId);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * dlAttempt));
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex,
                    "SignalOrderBridgeWorker: FAILED to dead-letter signal {Id} — signal stuck in Pending, manual intervention required",
                    signalId);

                await TryDispatchCriticalAlertAsync(signalId, reason, ex);
                return;
            }
        }
    }

    /// <summary>
    /// Best-effort alert dispatch when dead-lettering fails. If the alert itself fails,
    /// we log and move on — the <see cref="LogCritical"/> above is the last-resort record.
    /// </summary>
    private async Task TryDispatchCriticalAlertAsync(long signalId, string deadLetterReason, Exception originalEx)
    {
        try
        {
            using var scope    = _scopeFactory.CreateScope();
            var alertDispatcher = scope.ServiceProvider.GetRequiredService<IAlertDispatcher>();

            var alert = new Domain.Entities.Alert
            {
                AlertType = AlertType.DataQualityIssue,
                IsActive  = true,
            };

            await alertDispatcher.DispatchAsync(alert,
                $"[CRITICAL] SignalOrderBridgeWorker: failed to dead-letter signal {signalId}. " +
                $"Signal is stuck in Pending and requires manual intervention. " +
                $"Dead-letter reason: {deadLetterReason}. Error: {originalEx.GetType().Name}: {originalEx.Message}",
                CancellationToken.None);
        }
        catch (Exception alertEx)
        {
            _logger.LogError(alertEx,
                "SignalOrderBridgeWorker: critical alert dispatch also failed for signal {Id}",
                signalId);
        }
    }

    /// <summary>
    /// Determines whether an exception is transient and worth retrying.
    /// Database connectivity and timeout issues are transient; constraint violations,
    /// null references, and argument errors are permanent.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex is
        DbUpdateConcurrencyException or
        DBConcurrencyException or
        TimeoutException or
        IOException or
        DbUpdateException { InnerException: DbException { IsTransient: true } };

    private static Task RejectSignalAsync(IMediator mediator, long signalId, string reason, CancellationToken ct)
        => mediator.Send(new RejectTradeSignalCommand { Id = signalId, Reason = reason }, ct);

    public override void Dispose()
    {
        _shutdownCts.Dispose();
        _concurrencyLimiter.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
