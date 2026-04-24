using System.Diagnostics;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Orders.Commands.CreateOrder;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.CreateOrderFromSignal;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Creates an order from an approved trade signal for a specific trading account.
/// Runs Tier 2 (account-level) risk checks before order creation. If the risk check
/// fails, the signal stays Approved and a <see cref="SignalAccountAttempt"/> is recorded.
/// </summary>
public class CreateOrderFromSignalCommand : IRequest<ResponseData<long>>
{
    public long TradeSignalId { get; set; }
    public long TradingAccountId { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that both TradeSignalId and TradingAccountId are positive.</summary>
public class CreateOrderFromSignalCommandValidator : AbstractValidator<CreateOrderFromSignalCommand>
{
    public CreateOrderFromSignalCommandValidator()
    {
        RuleFor(x => x.TradeSignalId).GreaterThan(0);
        RuleFor(x => x.TradingAccountId).GreaterThan(0);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Loads the approved trade signal and target account, runs Tier 2 account-level risk checks,
/// evaluates entry timing, and delegates to <see cref="Orders.Commands.CreateOrder.CreateOrderCommand"/>
/// for order creation. Records a <see cref="Domain.Entities.SignalAccountAttempt"/> on both success and failure.
/// </summary>
public class CreateOrderFromSignalCommandHandler
    : IRequestHandler<CreateOrderFromSignalCommand, ResponseData<long>>
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly IRiskChecker _riskChecker;
    private readonly IMediator _mediator;
    private readonly ILivePriceCache _livePriceCache;
    private readonly ILogger<CreateOrderFromSignalCommandHandler> _logger;
    private readonly RiskCheckerOptions _riskOptions;
    private readonly TradingDayOptions _tradingDayOptions;
    private readonly TradingMetrics _metrics;
    private readonly IDegradationModeManager _degradationManager;
    private readonly IKillSwitchService _killSwitch;
    private readonly ILatencySlaRecorder? _latencySlaRecorder;

    public CreateOrderFromSignalCommandHandler(
        IReadApplicationDbContext readContext,
        IWriteApplicationDbContext writeContext,
        IRiskChecker riskChecker,
        IMediator mediator,
        ILivePriceCache livePriceCache,
        ILogger<CreateOrderFromSignalCommandHandler> logger,
        RiskCheckerOptions riskOptions,
        TradingDayOptions tradingDayOptions,
        TradingMetrics metrics,
        IDegradationModeManager degradationManager,
        IKillSwitchService killSwitch,
        ILatencySlaRecorder? latencySlaRecorder = null)
    {
        _readContext         = readContext;
        _writeContext        = writeContext;
        _riskChecker         = riskChecker;
        _mediator            = mediator;
        _livePriceCache      = livePriceCache;
        _logger              = logger;
        _riskOptions         = riskOptions;
        _tradingDayOptions   = tradingDayOptions;
        _metrics             = metrics;
        _degradationManager  = degradationManager;
        _killSwitch          = killSwitch;
        _latencySlaRecorder  = latencySlaRecorder;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="expiresAt"/> is past <c>UtcNow</c> by
    /// more than <see cref="RiskCheckerOptions.ClockSkewToleranceSeconds"/>.
    /// Protects against false-positive expiry when the engine and upstream
    /// producer clocks drift by a few seconds.
    /// </summary>
    private bool IsExpired(DateTime expiresAt, string stage)
    {
        var nowUtc = DateTime.UtcNow;
        var tolerance = Math.Max(0, _riskOptions.ClockSkewToleranceSeconds);
        bool expiredStrict = expiresAt <= nowUtc;
        bool expiredAfterTolerance = expiresAt <= nowUtc.AddSeconds(-tolerance);

        if (expiredStrict && !expiredAfterTolerance)
        {
            // The signal would have been rejected absent the tolerance window.
            _metrics.SignalExpirySkewToleranceApplied.Add(1,
                new KeyValuePair<string, object?>("stage", stage));
        }
        return expiredAfterTolerance;
    }

    public async Task<ResponseData<long>> Handle(
        CreateOrderFromSignalCommand request,
        CancellationToken cancellationToken)
    {
        // ── Degradation mode / kill switch gate ───────────────────────────
        // EmergencyHalt aborts every new order regardless of Tier-2 risk
        // results. The global kill switch is a softer operator override
        // (same effect for new orders, but existing positions unaffected).
        if (_degradationManager.CurrentMode == DegradationMode.EmergencyHalt)
        {
            _metrics.SignalsRejected.Add(1,
                new KeyValuePair<string, object?>("reason", "degradation_emergency_halt"));
            return ResponseData<long>.Init(0, false,
                "Engine is in EmergencyHalt — new orders suspended", "-11");
        }

        if (await _killSwitch.IsGlobalKilledAsync(cancellationToken))
        {
            _metrics.KillSwitchTriggered.Add(1,
                new KeyValuePair<string, object?>("scope", "global"),
                new KeyValuePair<string, object?>("site", "create_order_from_signal"));
            return ResponseData<long>.Init(0, false,
                "Global kill switch is active — new orders suspended", "-11");
        }

        var db = _readContext.GetDbContext();

        // ── Load signal ──────────────────────────────────────────────────────
        var signal = await db.Set<TradeSignal>()
            .Include(x => x.Strategy)
                .ThenInclude(s => s.RiskProfile)
            .FirstOrDefaultAsync(x => x.Id == request.TradeSignalId && !x.IsDeleted, cancellationToken);

        if (signal is null)
            return ResponseData<long>.Init(0, false, "Trade signal not found", "-14");

        if (signal.Status != TradeSignalStatus.Approved)
            return ResponseData<long>.Init(0, false,
                $"Signal is not in Approved status (current: {signal.Status})", "-11");

        if (IsExpired(signal.ExpiresAt, stage: "tier2"))
            return ResponseData<long>.Init(0, false, "Signal has expired", "-11");

        // Per-strategy kill switch: honour operator actions that happened
        // between signal creation and Tier-2 execution.
        if (await _killSwitch.IsStrategyKilledAsync(signal.StrategyId, cancellationToken))
        {
            _metrics.KillSwitchTriggered.Add(1,
                new KeyValuePair<string, object?>("scope", "strategy"),
                new KeyValuePair<string, object?>("site", "create_order_from_signal"));
            return ResponseData<long>.Init(0, false,
                $"Strategy {signal.StrategyId} kill switch is active", "-11");
        }

        // ── Tier-2 price staleness check ─────────────────────────────────────
        // Between signal generation and Tier-2 execution the market may have
        // moved far enough that filling at the stale EntryPrice would land the
        // trade in a different risk/reward profile than the evaluator analysed.
        // Compare live mid vs EntryPrice; reject when the drift exceeds the
        // configured cap (default 50 bps). Disabled when MaxEntryPriceDriftPct
        // == 0, when no live price is cached, or when EntryPrice is zero
        // (would imply divide-by-zero and is separately invalid).
        if (_riskOptions.MaxEntryPriceDriftPct > 0m && signal.EntryPrice > 0m)
        {
            var livePrice = _livePriceCache.Get(signal.Symbol);
            if (livePrice is not null)
            {
                decimal liveMid = (livePrice.Value.Bid + livePrice.Value.Ask) / 2m;
                decimal drift = Math.Abs(liveMid - signal.EntryPrice) / signal.EntryPrice;
                if (drift > _riskOptions.MaxEntryPriceDriftPct)
                {
                    _metrics.Tier2PriceDriftRejections.Add(1,
                        new KeyValuePair<string, object?>("symbol", signal.Symbol));
                    _logger.LogInformation(
                        "CreateOrderFromSignal: signal {SignalId} rejected — price drift {Drift:P3} > {Max:P3} (liveMid={Live}, entry={Entry})",
                        signal.Id, drift, _riskOptions.MaxEntryPriceDriftPct, liveMid, signal.EntryPrice);

                    await _mediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "TradeSignal",
                        EntityId     = signal.Id,
                        DecisionType = "OrderCreation",
                        Outcome      = "Rejected",
                        Reason       = $"Price drift {drift:P3} exceeds max {_riskOptions.MaxEntryPriceDriftPct:P3}",
                        Source       = "CreateOrderFromSignalCommand"
                    }, cancellationToken);

                    return ResponseData<long>.Init(0, false,
                        $"Live price has drifted {drift:P2} from signal entry price (max {_riskOptions.MaxEntryPriceDriftPct:P2}) — signal is stale",
                        "-11");
                }
            }
        }

        // ── Load trading account ─────────────────────────────────────────────
        var account = await db.Set<TradingAccount>()
            .FirstOrDefaultAsync(x => x.Id == request.TradingAccountId && !x.IsDeleted, cancellationToken);

        if (account is null)
            return ResponseData<long>.Init(0, false, "Trading account not found", "-14");

        // ── Check for previous attempt (pass or fail) ─────────────────────────
        bool alreadyAttempted = await db.Set<SignalAccountAttempt>()
            .AnyAsync(a => a.TradeSignalId == signal.Id
                        && a.TradingAccountId == account.Id
                        && !a.IsDeleted, cancellationToken);

        if (alreadyAttempted)
            return ResponseData<long>.Init(0, false,
                "This account has already attempted order creation for this signal", "-11");

        // ── Resolve risk profile ─────────────────────────────────────────────
        var riskProfile = signal.Strategy.RiskProfile
            ?? await db.Set<RiskProfile>()
                       .FirstOrDefaultAsync(x => x.IsDefault && !x.IsDeleted, cancellationToken);

        if (riskProfile is null)
            return ResponseData<long>.Init(0, false, "No risk profile configured", "-11");

        // ── Build Tier 2 risk check context ──────────────────────────────────
        var tier2LatencyStopwatch = Stopwatch.StartNew();
        var openPositions = await db.Set<Position>()
            .Where(p => p.Status == PositionStatus.Open && !p.IsDeleted)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var symbolSpec = await db.Set<CurrencyPair>()
            .FirstOrDefaultAsync(c => c.Symbol == signal.Symbol && !c.IsDeleted, cancellationToken);

        var nowUtc = DateTime.UtcNow;
        var tradingDayStartUtc = TradingDayBoundaryHelper.GetTradingDayStartUtc(
            nowUtc,
            _tradingDayOptions.RolloverMinuteOfDayUtc);
        int tradesToday = await db.Set<Order>()
            .CountAsync(o => o.CreatedAt >= tradingDayStartUtc && !o.IsDeleted, cancellationToken);

        // Count consecutive recent losses and track the timestamp of the last loss
        var recentClosedPositions = await db.Set<Position>()
            .Where(p => p.Status == PositionStatus.Closed && !p.IsDeleted)
            .OrderByDescending(p => p.ClosedAt)
            .Select(p => new { p.RealizedPnL, p.ClosedAt })
            .Take(50)
            .ToListAsync(cancellationToken);

        int consecutiveLosses = 0;
        DateTime? lastLossAt = null;
        foreach (var pos in recentClosedPositions)
        {
            if (pos.RealizedPnL < 0)
            {
                consecutiveLosses++;
                lastLossAt ??= pos.ClosedAt; // First (most recent) loss timestamp
            }
            else break;
        }

        // Daily starting balance
        decimal dailyStartBalance = 0m;
        var tradingDayBaseline = await TradingDayBoundaryHelper.ResolveStartOfDayEquityAsync(
            db,
            account.Id,
            nowUtc,
            _tradingDayOptions,
            cancellationToken);
        if (tradingDayBaseline is not null)
        {
            dailyStartBalance = tradingDayBaseline.StartOfDayEquity;
        }
        else
        {
            _logger.LogWarning(
                "CreateOrderFromSignal: no trusted trading-day baseline available for account {AccountId} at {TradingDayStart:o}; daily drawdown gates will be skipped",
                account.Id,
                tradingDayStartUtc);
        }

        // Current spread. An inverted quote (Ask < Bid) is a data-quality anomaly — the
        // feed is either mid-update or broken. RiskChecker rejects inverted quotes as a
        // hard fail (RiskChecker.cs ~line 144); we mirror that here at the Tier-2 entry
        // so the two tiers stay symmetric. Previously this path logged a warning and
        // continued with spread validation skipped, which meant a broken feed could
        // silently bypass the spread filter.
        decimal? currentSpread = null;
        try
        {
            var livePrice = _livePriceCache.Get(signal.Symbol);
            if (livePrice is not null)
            {
                if (livePrice.Value.Ask < livePrice.Value.Bid)
                {
                    _logger.LogWarning(
                        "CreateOrderFromSignal: inverted quote for {Symbol} (Bid={Bid}, Ask={Ask}) — rejecting as data-quality failure",
                        signal.Symbol, livePrice.Value.Bid, livePrice.Value.Ask);
                    await RecordAttemptAsync(signal.Id, account.Id, false,
                        $"Inverted quote for {signal.Symbol} (Bid={livePrice.Value.Bid}, Ask={livePrice.Value.Ask})",
                        cancellationToken);
                    return ResponseData<long>.Init(0, false,
                        $"Inverted quote for {signal.Symbol} — data-quality failure, rejecting order", "-11");
                }
                currentSpread = livePrice.Value.Ask - livePrice.Value.Bid;
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "CreateOrderFromSignal: spread unavailable for {Symbol}", signal.Symbol);
        }

        // Portfolio specs and conversion rates
        var allSymbols = openPositions.Select(p => p.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var allPortfolioSpecs = await db.Set<CurrencyPair>()
            .Where(c => allSymbols.Contains(c.Symbol) && !c.IsDeleted)
            .ToListAsync(cancellationToken);
        var symbolSpecs = allPortfolioSpecs.ToDictionary(c => c.Symbol, c => c.ContractSize);

        decimal? quoteToAccountRate = CurrencyConversionHelper.ResolveQuoteToAccountRate(
            symbolSpec, account.Currency, _livePriceCache);
        var portfolioQuoteToAccountRates = CurrencyConversionHelper.ResolvePortfolioQuoteToAccountRates(
            allPortfolioSpecs, account.Currency, _livePriceCache);

        // Read drawdown recovery mode from EngineConfig to activate lot-size reduction
        bool isInRecoveryMode = false;
        var recoveryModeConfig = await db.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == "DrawdownRecovery:ActiveMode", cancellationToken);
        if (recoveryModeConfig is not null
            && Enum.TryParse<RecoveryMode>(recoveryModeConfig.Value, ignoreCase: true, out var recoveryMode)
            && recoveryMode is RecoveryMode.Reduced or RecoveryMode.Halted)
        {
            isInRecoveryMode = true;
        }

        var riskContext = new RiskCheckContext
        {
            Account                     = account,
            Profile                     = riskProfile,
            OpenPositions               = openPositions,
            SymbolSpec                  = symbolSpec,
            TradesToday                 = tradesToday,
            ConsecutiveLosses           = consecutiveLosses,
            LastLossAt                  = lastLossAt,
            CurrentSpread               = currentSpread,
            DailyStartBalance           = dailyStartBalance,
            PortfolioContractSizes      = symbolSpecs,
            QuoteToAccountRate          = quoteToAccountRate,
            PortfolioQuoteToAccountRates = portfolioQuoteToAccountRates,
            IsInRecoveryMode            = isInRecoveryMode,
        };

        // ── Run Tier 2 risk check ────────────────────────────────────────────
        var riskResult = await _riskChecker.CheckAsync(signal, riskContext, cancellationToken);
        tier2LatencyStopwatch.Stop();
        _latencySlaRecorder?.RecordSample(
            LatencySlaSegments.Tier2RiskCheck,
            (long)Math.Round(tier2LatencyStopwatch.Elapsed.TotalMilliseconds));

        if (!riskResult.Passed)
        {
            // Record failed attempt
            await RecordAttemptAsync(signal.Id, account.Id, false, riskResult.BlockReason, cancellationToken);

            _logger.LogInformation(
                "CreateOrderFromSignal: signal {SignalId} blocked for account {AccountId} — {Reason}",
                signal.Id, account.Id, riskResult.BlockReason);

            await _mediator.Send(new LogDecisionCommand
            {
                EntityType   = "TradeSignal",
                EntityId     = signal.Id,
                DecisionType = "AccountRiskCheck",
                Outcome      = "Rejected",
                Reason       = $"Account {account.AccountId}: {riskResult.BlockReason}",
                Source       = "CreateOrderFromSignalCommand"
            }, cancellationToken);

            return ResponseData<long>.Init(0, false, riskResult.BlockReason!, "-11");
        }

        // ── Entry timing evaluation ──────────────────────────────────────────
        var executionDelay = EntryTimingEvaluator.Evaluate(signal, _livePriceCache, _logger);
        if (executionDelay > TimeSpan.Zero)
        {
            _logger.LogInformation(
                "CreateOrderFromSignal: delaying execution of signal {SignalId} by {Delay}ms",
                signal.Id, executionDelay.TotalMilliseconds);
            await Task.Delay(executionDelay, cancellationToken);
        }

        // ── Re-validate signal expiry after risk checks + timing delay ──────
        // Signal could have expired during the risk check and entry timing evaluation
        // window. Re-check before committing to order creation. Clock-skew tolerance
        // applies here too — the same signal accepted at Tier-2 entry shouldn't flip
        // to "expired" a few hundred milliseconds later just because the engine
        // clock rolled past the nominal ExpiresAt during risk/timing work.
        if (IsExpired(signal.ExpiresAt, stage: "reentry"))
            return ResponseData<long>.Init(0, false,
                "Signal expired during risk check processing (expiry race)", "-11");

        // ── Create order ─────────────────────────────────────────────────────
        // CreateOrderCommand internally uses SaveAndPublish which opens its own
        // ResilientTransaction. We must NOT wrap this in an outer execution strategy
        // + BeginTransaction — nested execution strategies with NpgsqlRetryingExecutionStrategy
        // throw InvalidOperationException.
        var wdb = _writeContext.GetDbContext();

        // ── Orphan reaper ─────────────────────────────────────────────────────
        // Guards the narrow crash window between CreateOrderCommand (which commits
        // an Order with TradeSignalId = signal.Id) and the linkback ExecuteUpdate
        // below. If the engine crashes in that window, the Order stays Pending
        // with no signal back-reference and the TradeSignal has OrderId = null.
        // The SignalAccountAttempt is also not yet recorded, so a retry would
        // pass the alreadyAttempted check and create a second Order — leaking
        // duplicates. Cancel any such orphan here before creating a new one.
        // Runs only when signal.OrderId is still null (the post-linkback state
        // would have bounced us at the "alreadyAttempted" or status guard).
        if (signal.OrderId is null)
        {
            int reaped = await wdb.Set<Order>()
                .Where(o => o.TradeSignalId == signal.Id
                         && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Submitted)
                         && !o.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OrderStatus.Cancelled)
                    .SetProperty(o => o.Notes,  "Cancelled: orphaned — prior attempt crashed before signal linkback"),
                    cancellationToken);
            if (reaped > 0)
            {
                _logger.LogWarning(
                    "CreateOrderFromSignal: reaped {Count} orphaned order(s) for signal {SignalId} " +
                    "(prior attempt crashed between order create and linkback)",
                    reaped, signal.Id);
            }
        }

        // Use the per-account resolved lot if Tier 2 rewrote it (drawdown recovery cap).
        // The risk checker does NOT mutate signal.SuggestedLotSize — doing so would leak
        // one account's cap into the next account's Tier 2 evaluation. null means "no
        // rewrite, use the signal value as-is".
        decimal quantity = riskResult.ResolvedLotSize ?? signal.SuggestedLotSize;
        var orderResult = await _mediator.Send(new CreateOrderCommand
        {
            TradeSignalId    = signal.Id,
            StrategyId       = signal.StrategyId,
            TradingAccountId = account.Id,
            Symbol           = signal.Symbol,
            OrderType        = signal.Direction.ToString(),
            ExecutionType    = "Market",
            Quantity         = quantity,
            Price            = 0,
            StopLoss         = signal.StopLoss,
            TakeProfit       = signal.TakeProfit,
            IsPaper          = account.IsPaper
        }, cancellationToken);

        if (!orderResult.status || orderResult.data <= 0)
        {
            _logger.LogError("CreateOrderFromSignal: CreateOrderCommand failed for signal {SignalId}", signal.Id);
            await RecordAttemptAsync(signal.Id, account.Id, false, "CreateOrderCommand failed", cancellationToken);
            return ResponseData<long>.Init(0, false, "Failed to create order", "-11");
        }

        long orderId = orderResult.data;

        // Write OrderId back to signal atomically with status guard — prevents concurrent
        // consumers from both succeeding. Only updates if signal is still Approved with no OrderId.
        // If this fails, cancel the order to prevent orphans.
        try
        {
            int updated = await wdb.Set<TradeSignal>()
                .Where(x => x.Id == signal.Id
                          && x.Status == TradeSignalStatus.Approved
                          && x.OrderId == null
                          && !x.IsDeleted)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.OrderId, orderId), cancellationToken);

            if (updated == 0)
            {
                _logger.LogWarning(
                    "CreateOrderFromSignal: signal {SignalId} was consumed by another request — cancelling order {OrderId}",
                    signal.Id, orderId);

                var raceOrder = await wdb.Set<Order>()
                    .FirstOrDefaultAsync(o => o.Id == orderId && !o.IsDeleted, cancellationToken);
                if (raceOrder is not null)
                {
                    raceOrder.Status = OrderStatus.Cancelled;
                    raceOrder.Notes = "Cancelled: signal consumed by concurrent request";
                    await _writeContext.SaveChangesAsync(cancellationToken);
                }

                await RecordAttemptAsync(signal.Id, account.Id, false, "Signal already consumed (race)", cancellationToken);
                return ResponseData<long>.Init(0, false, "Signal already consumed by another request", "-11");
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "CreateOrderFromSignal: failed to link order {OrderId} to signal {SignalId} — cancelling order to prevent orphan",
                orderId, signal.Id);

            // Cancel the orphaned order so it doesn't execute without signal linkage
            var orphanedOrder = await wdb.Set<Order>()
                .FirstOrDefaultAsync(o => o.Id == orderId && !o.IsDeleted, cancellationToken);
            if (orphanedOrder is not null)
            {
                orphanedOrder.Status = OrderStatus.Cancelled;
                orphanedOrder.Notes = $"Cancelled: signal link-back failed — {ex.Message}";
                await _writeContext.SaveChangesAsync(cancellationToken);
            }

            await RecordAttemptAsync(signal.Id, account.Id, false, $"Signal link-back failed: {ex.Message}", cancellationToken);
            return ResponseData<long>.Init(0, false, "Order created but signal link failed — order cancelled", "-11");
        }

        await RecordAttemptAsync(signal.Id, account.Id, true, null, cancellationToken);

        await _mediator.Send(new LogDecisionCommand
        {
            EntityType   = "Order",
            EntityId     = orderId,
            DecisionType = "OrderCreated",
            Outcome      = "Pending",
            Reason       = $"Signal {signal.Id} — {signal.Direction} {signal.Symbol} lot={signal.SuggestedLotSize}, account={account.AccountId}",
            Source       = "CreateOrderFromSignalCommand"
        }, cancellationToken);

        if (orderId <= 0)
            return ResponseData<long>.Init(0, false, "Failed to create order", "-11");

        _logger.LogInformation(
            "CreateOrderFromSignal: order {OrderId} created from signal {SignalId} for account {AccountId}",
            orderId, signal.Id, account.Id);

        return ResponseData<long>.Init(orderId, true, "Successful", "00");
    }

    private async Task RecordAttemptAsync(
        long signalId, long accountId, bool passed, string? blockReason, CancellationToken ct)
    {
        var attempt = new SignalAccountAttempt
        {
            TradeSignalId    = signalId,
            TradingAccountId = accountId,
            Passed           = passed,
            BlockReason      = blockReason,
            AttemptedAt      = DateTime.UtcNow
        };

        await _writeContext.GetDbContext()
            .Set<SignalAccountAttempt>()
            .AddAsync(attempt, ct);
        await _writeContext.SaveChangesAsync(ct);
    }
}
