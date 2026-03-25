using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
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

public class CreateOrderFromSignalCommandValidator : AbstractValidator<CreateOrderFromSignalCommand>
{
    public CreateOrderFromSignalCommandValidator()
    {
        RuleFor(x => x.TradeSignalId).GreaterThan(0);
        RuleFor(x => x.TradingAccountId).GreaterThan(0);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class CreateOrderFromSignalCommandHandler
    : IRequestHandler<CreateOrderFromSignalCommand, ResponseData<long>>
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly IRiskChecker _riskChecker;
    private readonly IMediator _mediator;
    private readonly ILivePriceCache _livePriceCache;
    private readonly ILogger<CreateOrderFromSignalCommandHandler> _logger;

    public CreateOrderFromSignalCommandHandler(
        IReadApplicationDbContext readContext,
        IWriteApplicationDbContext writeContext,
        IRiskChecker riskChecker,
        IMediator mediator,
        ILivePriceCache livePriceCache,
        ILogger<CreateOrderFromSignalCommandHandler> logger)
    {
        _readContext    = readContext;
        _writeContext   = writeContext;
        _riskChecker   = riskChecker;
        _mediator      = mediator;
        _livePriceCache = livePriceCache;
        _logger        = logger;
    }

    public async Task<ResponseData<long>> Handle(
        CreateOrderFromSignalCommand request,
        CancellationToken cancellationToken)
    {
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

        if (signal.ExpiresAt <= DateTime.UtcNow)
            return ResponseData<long>.Init(0, false, "Signal has expired", "-11");

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
        var openPositions = await db.Set<Position>()
            .Where(p => p.Status == PositionStatus.Open && !p.IsDeleted)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var symbolSpec = await db.Set<CurrencyPair>()
            .FirstOrDefaultAsync(c => c.Symbol == signal.Symbol && !c.IsDeleted, cancellationToken);

        var todayStart = DateTime.UtcNow.Date;
        int tradesToday = await db.Set<Order>()
            .CountAsync(o => o.CreatedAt >= todayStart && !o.IsDeleted, cancellationToken);

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
        decimal dailyStartBalance = account.Balance;
        var todaySnapshot = await db.Set<DrawdownSnapshot>()
            .Where(s => s.RecordedAt >= todayStart)
            .OrderBy(s => s.RecordedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (todaySnapshot is not null)
            dailyStartBalance = todaySnapshot.CurrentEquity;

        // Current spread
        decimal? currentSpread = null;
        try
        {
            var livePrice = _livePriceCache.Get(signal.Symbol);
            if (livePrice is not null)
            {
                if (livePrice.Value.Ask < livePrice.Value.Bid)
                    _logger.LogWarning("CreateOrderFromSignal: inverted quote for {Symbol} (Bid={Bid}, Ask={Ask}) — skipping spread check",
                        signal.Symbol, livePrice.Value.Bid, livePrice.Value.Ask);
                else
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

        // ── Create order ─────────────────────────────────────────────────────
        // Wrap order creation + signal linkage + attempt recording in an explicit
        // transaction so that all three writes succeed or fail atomically.
        var wdb = _writeContext.GetDbContext();
        await using var transaction = await wdb.Database.BeginTransactionAsync(cancellationToken);

        var orderResult = await _mediator.Send(new CreateOrderCommand
        {
            TradeSignalId    = signal.Id,
            StrategyId       = signal.StrategyId,
            TradingAccountId = account.Id,
            Symbol           = signal.Symbol,
            OrderType        = signal.Direction.ToString(),
            ExecutionType    = "Market",
            Quantity         = signal.SuggestedLotSize,
            Price            = 0,
            StopLoss         = signal.StopLoss,
            TakeProfit       = signal.TakeProfit,
            IsPaper          = account.IsPaper
        }, cancellationToken);

        if (!orderResult.status || orderResult.data <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError("CreateOrderFromSignal: CreateOrderCommand failed for signal {SignalId}", signal.Id);
            return ResponseData<long>.Init(0, false, "Failed to create order", "-11");
        }

        // Write OrderId back to signal
        var signalEntity = await wdb.Set<TradeSignal>()
            .FirstOrDefaultAsync(x => x.Id == signal.Id && !x.IsDeleted, cancellationToken);
        if (signalEntity is not null)
        {
            signalEntity.OrderId = orderResult.data;
            await _writeContext.SaveChangesAsync(cancellationToken);
        }

        // Record successful attempt and audit log inside the transaction so all
        // three writes (order, attempt, audit) succeed or fail atomically.
        await RecordAttemptAsync(signal.Id, account.Id, true, null, cancellationToken);

        await _mediator.Send(new LogDecisionCommand
        {
            EntityType   = "Order",
            EntityId     = orderResult.data,
            DecisionType = "OrderCreated",
            Outcome      = "Pending",
            Reason       = $"Signal {signal.Id} — {signal.Direction} {signal.Symbol} lot={signal.SuggestedLotSize}, account={account.AccountId}",
            Source       = "CreateOrderFromSignalCommand"
        }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "CreateOrderFromSignal: order {OrderId} created from signal {SignalId} for account {AccountId}",
            orderResult.data, signal.Id, account.Id);

        return ResponseData<long>.Init(orderResult.data, true, "Successful", "00");
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
