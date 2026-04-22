using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.RiskProfiles.Services;

/// <summary>
/// Tier 2 account-level risk checker — validates a trade signal against a specific trading
/// account's state including margin availability, position limits, exposure caps, drawdown gates,
/// spread checks, correlation limits, cross-currency conversion, lot size validation,
/// and weekend/holiday gap risk adjustments.
/// <para>
/// Signal-level (Tier 1) checks (expiry, SL/TP consistency, R:R, ML agreement) are handled
/// by <see cref="SignalValidator"/> and should run before this checker is invoked.
/// </para>
/// <para>
/// This class is registered as a concrete type in DI and wrapped by
/// <see cref="RiskCheckerPipeline"/>, which runs all <see cref="IRiskCheckStep"/>
/// implementations before delegating to this monolithic checker as the final fallback.
/// </para>
/// </summary>
public class RiskChecker : IRiskChecker
{
    private readonly RiskCheckerOptions _options;
    private readonly CorrelationGroupOptions _correlationGroups;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RiskChecker> _logger;
    private readonly IReadApplicationDbContext _readDb;
    private readonly IPortfolioRiskCalculator? _portfolioRisk;
    private readonly IGapRiskModel? _gapRiskModel;

    public RiskChecker(
        RiskCheckerOptions options,
        CorrelationGroupOptions correlationGroups,
        TimeProvider timeProvider,
        ILogger<RiskChecker> logger,
        IReadApplicationDbContext readDb,
        IPortfolioRiskCalculator? portfolioRisk = null,
        IGapRiskModel? gapRiskModel = null)
    {
        _options = options;
        _correlationGroups = correlationGroups;
        _timeProvider = timeProvider;
        _logger = logger;
        _readDb = readDb;
        _portfolioRisk = portfolioRisk;
        _gapRiskModel = gapRiskModel;
    }

    public async Task<RiskCheckResult> CheckAsync(
        TradeSignal signal,
        RiskCheckContext context,
        CancellationToken cancellationToken)
    {
        var profile = context.Profile;
        var account = context.Account;

        // ── 0. Equity guard — prevent division-by-zero and nonsensical calculations ──
        if (account.Equity <= 0)
            return Fail(
                $"Account equity is {account.Equity:F2} {account.Currency} (non-positive). " +
                "All trading is halted until equity is restored.");

        // ── 1. Symbol spec required ─────────────────────────────────────────
        if (context.SymbolSpec is null)
            return Fail($"No symbol specification found for {signal.Symbol}. Cannot calculate margin or risk without contract size.");

        decimal contractSize = context.SymbolSpec.ContractSize;

        // Working copy of the lot size used by this evaluation. NEVER write back to
        // signal.SuggestedLotSize — the signal is shared across per-account attempts
        // and mutating it leaks one account's recovery cap into another account's
        // next Tier 2 run. The caller will read either resolvedLot (when the recovery
        // cap applied) or signal.SuggestedLotSize (when it didn't) via the
        // ResolvedLotSize field on RiskCheckResult.
        decimal resolvedLot = signal.SuggestedLotSize;

        // ── 2. Minimum lot size (broker constraint) ─────────────────────────
        if (context.SymbolSpec.MinLotSize > 0 && signal.SuggestedLotSize < context.SymbolSpec.MinLotSize)
            return Fail(
                $"Lot size {signal.SuggestedLotSize} is below the broker minimum of {context.SymbolSpec.MinLotSize} for {signal.Symbol}.");

        // ── 3. Lot step validation (broker constraint) ──────────────────────
        if (context.SymbolSpec.LotStep > 0)
        {
            decimal remainder = signal.SuggestedLotSize % context.SymbolSpec.LotStep;
            if (remainder > 0.000001m) // tolerance for floating point
                return Fail(
                    $"Lot size {signal.SuggestedLotSize} is not a valid increment of lot step {context.SymbolSpec.LotStep} for {signal.Symbol}. " +
                    $"Nearest valid sizes: {Math.Floor(signal.SuggestedLotSize / context.SymbolSpec.LotStep) * context.SymbolSpec.LotStep} or " +
                    $"{Math.Ceiling(signal.SuggestedLotSize / context.SymbolSpec.LotStep) * context.SymbolSpec.LotStep}.");
        }

        // ── 4. Broker max lot size ──────────────────────────────────────────
        if (context.SymbolSpec.MaxLotSize > 0 && signal.SuggestedLotSize > context.SymbolSpec.MaxLotSize)
            return Fail(
                $"Lot size {signal.SuggestedLotSize} exceeds broker maximum of {context.SymbolSpec.MaxLotSize} for {signal.Symbol}.");

        // ── 5. Lot size cap (with recovery mode reduction) ──────────────────
        decimal effectiveMaxLot = profile.MaxLotSizePerTrade;
        if (context.IsInRecoveryMode)
        {
            decimal clampedMultiplier = Math.Clamp(profile.RecoveryLotSizeMultiplier, 0.01m, 1.0m);
            effectiveMaxLot *= clampedMultiplier;
        }

        if (signal.SuggestedLotSize > effectiveMaxLot)
            return Fail(context.IsInRecoveryMode
                ? $"Lot size {signal.SuggestedLotSize} exceeds recovery-adjusted maximum " +
                  $"{effectiveMaxLot} (base {profile.MaxLotSizePerTrade} × " +
                  $"{Math.Clamp(profile.RecoveryLotSizeMultiplier, 0.01m, 1.0m)} recovery multiplier)."
                : $"Lot size {signal.SuggestedLotSize} exceeds profile maximum {effectiveMaxLot}.");

        // ── 5b. Drawdown recovery lot reduction (Tier 2 enforcement) ────────
        // If the account is in Reduced recovery mode, cap the effective lot size
        // for THIS account only. We update `resolvedLot` (local working copy) instead
        // of mutating signal.SuggestedLotSize, because the same signal may be evaluated
        // against other accounts that are NOT in recovery — they must see the original
        // lot. The caller propagates the resolved value via RiskCheckResult.ResolvedLotSize.
        {
            string recoveryMode = await GetRecoveryModeAsync(cancellationToken);
            if (string.Equals(recoveryMode, "Reduced", StringComparison.OrdinalIgnoreCase))
            {
                decimal multiplier = await GetRecoveryMultiplierAsync(cancellationToken);
                if (multiplier < 1.0m)
                {
                    // Don't reject — just cap. The signal is valid, just oversized for recovery mode.
                    decimal originalLot = resolvedLot;
                    resolvedLot = originalLot * multiplier;
                    _logger.LogInformation(
                        "Drawdown recovery Tier 2: lot capped from {OriginalLot} to {Lot} (x{Mult}) for account {Account}",
                        originalLot, resolvedLot, multiplier, account.Id);
                }
            }
            else if (string.Equals(recoveryMode, "Halted", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("Account is in Halted recovery mode — all new orders blocked.");
            }
        }

        // Post-cap validation: a deep recovery cap can push the lot below broker minimums
        // or off the lot-step grid. Reject explicitly rather than letting the broker
        // reject an invalid order — caller gets a clean rejection reason and no partial
        // state on either side.
        if (context.SymbolSpec.MinLotSize > 0 && resolvedLot < context.SymbolSpec.MinLotSize)
            return Fail(
                $"Drawdown recovery cap reduced lot to {resolvedLot}, below broker minimum {context.SymbolSpec.MinLotSize} for {signal.Symbol}. " +
                "Increase recovery multiplier or wait for account exit from Reduced mode.");
        if (context.SymbolSpec.LotStep > 0)
        {
            decimal remainderAfterCap = resolvedLot % context.SymbolSpec.LotStep;
            if (remainderAfterCap != 0m)
                return Fail(
                    $"Drawdown recovery cap produced lot {resolvedLot} off the broker lot-step grid ({context.SymbolSpec.LotStep}) for {signal.Symbol}. " +
                    "Adjust recovery multiplier to align with lot step.");
        }

        // ── 6. Minimum equity floor ─────────────────────────────────────────
        if (profile.MinEquityFloor > 0 && account.Equity < profile.MinEquityFloor)
            return Fail(
                $"Account equity {account.Equity:F2} {account.Currency} is below the minimum floor of " +
                $"{profile.MinEquityFloor:F2} {account.Currency}. All trading is halted.");

        // ── 7. Spread validation ────────────────────────────────────────────
        if (_options.MaxSpreadPips > 0 && context.CurrentSpread.HasValue)
        {
            // Reject inverted spreads (Ask < Bid)
            if (context.CurrentSpread.Value < 0)
                return Fail(
                    $"Inverted quote detected for {signal.Symbol} (spread={context.CurrentSpread.Value:F5}). " +
                    "Data quality issue — rejecting order.");

            decimal pipSize = GetPipSize(context.SymbolSpec);
            decimal spreadPips = pipSize > 0 ? context.CurrentSpread.Value / pipSize : 0;

            if (spreadPips > _options.MaxSpreadPips)
                return Fail(
                    $"Current spread of {spreadPips:F1} pips on {signal.Symbol} exceeds the maximum of " +
                    $"{_options.MaxSpreadPips} pips. Market may be illiquid.");
        }

        // ── 8. Consecutive loss streak gate (with auto-reset cooldown) ─────
        if (profile.MaxConsecutiveLosses > 0 && context.ConsecutiveLosses >= profile.MaxConsecutiveLosses)
        {
            // Auto-reset after cooldown period to prevent permanent deadlock
            bool cooledDown = false;
            if (_options.ConsecutiveLossCooldownMinutes > 0 && context.LastLossAt.HasValue)
            {
                var elapsed = _timeProvider.GetUtcNow().UtcDateTime - context.LastLossAt.Value;
                cooledDown = elapsed.TotalMinutes >= _options.ConsecutiveLossCooldownMinutes;
            }

            if (!cooledDown)
                return Fail(
                    $"Consecutive loss streak of {context.ConsecutiveLosses} has reached the limit of " +
                    $"{profile.MaxConsecutiveLosses}. Trading is paused until the streak resets " +
                    $"(auto-reset after {_options.ConsecutiveLossCooldownMinutes} minutes from last loss).");
            else
                _logger.LogInformation(
                    "RiskChecker: consecutive loss streak of {Streak} exceeded limit {Max} but cooldown " +
                    "of {Cooldown}m has elapsed since last loss at {LastLoss} — allowing trade",
                    context.ConsecutiveLosses, profile.MaxConsecutiveLosses,
                    _options.ConsecutiveLossCooldownMinutes, context.LastLossAt);
        }

        // ── 9. Max open positions ───────────────────────────────────────────
        int effectivePositionCount = CountEffectivePositions(context.OpenPositions, account.MarginMode);
        if (effectivePositionCount >= profile.MaxOpenPositions)
            return Fail(
                $"Open position count ({effectivePositionCount}) has reached the limit of {profile.MaxOpenPositions}.");

        // ── 10. Per-symbol max positions ───────────────────────────────────
        if (profile.MaxPositionsPerSymbol > 0)
        {
            int symbolPositionCount = context.OpenPositions
                .Count(p => string.Equals(p.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase));

            if (symbolPositionCount >= profile.MaxPositionsPerSymbol)
                return Fail(
                    $"Position count for {signal.Symbol} ({symbolPositionCount}) has reached the per-symbol limit of " +
                    $"{profile.MaxPositionsPerSymbol}.");
        }

        // ── 11. Max daily trades ────────────────────────────────────────────
        if (context.TradesToday >= profile.MaxDailyTrades)
            return Fail(
                $"Daily trade count ({context.TradesToday}) has reached the limit of {profile.MaxDailyTrades}.");

        // ── 12. Daily drawdown gate ────────────────────────────────────────
        if (context.DailyStartBalance > 0 && account.Equity > 0)
        {
            decimal dailyDrawdownPct = (context.DailyStartBalance - account.Equity) / context.DailyStartBalance * 100m;
            if (dailyDrawdownPct >= profile.MaxDailyDrawdownPct)
                return Fail(
                    $"Daily drawdown of {dailyDrawdownPct:F2}% (from today's start balance {context.DailyStartBalance:F2}) " +
                    $"has reached or exceeded the limit of {profile.MaxDailyDrawdownPct}%. No further orders will be placed today.");
        }

        // ── 13. Absolute daily loss cap (account-level) ─────────────────────
        if (account.MaxAbsoluteDailyLoss > 0 && context.DailyStartBalance > 0)
        {
            decimal dailyLoss = context.DailyStartBalance - account.Equity;
            if (dailyLoss >= account.MaxAbsoluteDailyLoss)
                return Fail(
                    $"Daily loss of {dailyLoss:F2} {account.Currency} has reached or exceeded the absolute cap of " +
                    $"{account.MaxAbsoluteDailyLoss:F2} {account.Currency}. No further orders will be placed today.");
        }

        // ── 14. Correlated exposure check ──────────────────────────────────
        if (profile.MaxCorrelatedPositions > 0)
        {
            int correlatedCount = CountCorrelatedPositions(
                context.OpenPositions, signal.Symbol,
                _correlationGroups.Groups, context.ComputedCorrelations);
            if (correlatedCount >= profile.MaxCorrelatedPositions)
                return Fail(
                    $"Correlated position count for {signal.Symbol}'s group ({correlatedCount}) " +
                    $"has reached the limit of {profile.MaxCorrelatedPositions}.");
        }

        // ── Cross-currency conversion rate ──────────────────────────────────
        decimal quoteToAccountRate;
        if (context.QuoteToAccountRate is null)
        {
            // Safe default only when the account currency matches the quote currency of the pair.
            // For cross-currency pairs (e.g. GBPJPY on a USD account) the caller must supply the rate.
            bool isSameQuoteCurrency = signal.Symbol.Length >= 6
                && signal.Symbol[3..6].Equals(account.Currency, StringComparison.OrdinalIgnoreCase);
            if (!isSameQuoteCurrency)
                return Fail($"QuoteToAccountRate is required for cross-currency pair {signal.Symbol} on {account.Currency} account.");
            quoteToAccountRate = 1.0m;
        }
        else
        {
            quoteToAccountRate = context.QuoteToAccountRate.Value;
        }
        if (quoteToAccountRate <= 0)
            return Fail($"Invalid quote-to-account conversion rate ({quoteToAccountRate}) for {signal.Symbol}. Cannot calculate margin.");
        decimal slippageBuffer = _options.SlippageBufferMultiplier;

        // ── 15. Symbol exposure check (margin-mode-aware, currency-converted) ──
        if (account.Equity > 0 && account.Leverage > 0)
        {
            decimal symbolExposureLots = CalculateSymbolExposureLots(
                context.OpenPositions, signal.Symbol, signal.Direction, account.MarginMode);
            decimal proposedLots = symbolExposureLots + resolvedLot;
            decimal symbolMargin = proposedLots * contractSize * signal.EntryPrice * quoteToAccountRate / account.Leverage;
            decimal exposurePct = symbolMargin / account.Equity * 100m;

            if (exposurePct > profile.MaxSymbolExposurePct)
                return Fail(
                    $"Symbol margin exposure for {signal.Symbol} would be {exposurePct:F1}% of equity, " +
                    $"exceeding limit of {profile.MaxSymbolExposurePct}%.");
        }

        // ── 16. Total portfolio exposure check (currency-converted) ─────────
        if (account.Equity > 0 && account.Leverage > 0 && profile.MaxTotalExposurePct > 0)
        {
            decimal totalMargin = CalculateTotalPortfolioMargin(
                context.OpenPositions, account.Leverage,
                context.PortfolioContractSizes, context.PortfolioQuoteToAccountRates);
            decimal newTradeMargin = resolvedLot * contractSize * signal.EntryPrice * quoteToAccountRate / account.Leverage;
            decimal projectedTotalMargin = totalMargin + newTradeMargin;
            decimal totalExposurePct = projectedTotalMargin / account.Equity * 100m;

            if (totalExposurePct > profile.MaxTotalExposurePct)
                return Fail(
                    $"Total portfolio exposure would be {totalExposurePct:F1}% of equity, " +
                    $"exceeding limit of {profile.MaxTotalExposurePct}%.");
        }

        // ── 17. Risk per trade check (currency-converted, with slippage + gap) ──
        if (signal.StopLoss.HasValue && account.Equity > 0)
        {
            decimal riskPips = Math.Abs(signal.EntryPrice - signal.StopLoss.Value);

            // SL == Entry → zero risk distance — meaningless stop loss
            if (riskPips < 0.0000000001m)
                return Fail("StopLoss is equal to entry price — zero risk distance.");
            decimal riskAmount = resolvedLot * contractSize * riskPips * quoteToAccountRate * slippageBuffer;

            // Apply weekend/holiday gap risk multiplier if within the gap window.
            // Use the dynamic gap risk model when available; take the higher of static vs dynamic
            // to avoid accidentally downgrading protection when the dynamic model returns a lower value.
            decimal gapMultiplier = GetGapRiskMultiplier(profile.WeekendGapRiskMultiplier);
            if (gapMultiplier > 1.0m && _gapRiskModel is not null)
            {
                var gapEstimate = await _gapRiskModel.GetGapMultiplierAsync(signal.Symbol, cancellationToken);
                gapMultiplier = Math.Max(gapMultiplier, gapEstimate.GapMultiplier);
            }
            if (gapMultiplier > 1.0m)
                riskAmount *= gapMultiplier;

            decimal riskPct = riskAmount / account.Equity * 100m;

            if (riskPct > profile.MaxRiskPerTradePct)
                return Fail(
                    gapMultiplier > 1.0m
                        ? $"Risk per trade would be {riskPct:F2}% of equity ({riskAmount:F2} {account.Currency}), " +
                          $"exceeding limit of {profile.MaxRiskPerTradePct}% " +
                          $"(includes {gapMultiplier:F1}x gap risk multiplier)."
                        : $"Risk per trade would be {riskPct:F2}% of equity ({riskAmount:F2} {account.Currency}), " +
                          $"exceeding limit of {profile.MaxRiskPerTradePct}%.");

            // Absolute dollar risk cap
            if (profile.MaxAbsoluteRiskPerTrade > 0 && riskAmount > profile.MaxAbsoluteRiskPerTrade)
                return Fail(
                    $"Risk per trade of {riskAmount:F2} {account.Currency} exceeds the absolute cap of " +
                    $"{profile.MaxAbsoluteRiskPerTrade:F2} {account.Currency}.");
        }

        // ── 18. Margin sufficiency check (currency-converted, with slippage) ──
        if (account.Leverage > 0)
        {
            decimal requiredMargin = resolvedLot * contractSize * signal.EntryPrice * quoteToAccountRate
                / account.Leverage * slippageBuffer;

            // On netting accounts, an opposite-direction trade reduces margin rather than
            // adding to it. Calculate the net margin impact.
            if (account.MarginMode == MarginMode.Netting)
            {
                var existingOnSymbol = context.OpenPositions
                    .Where(p => string.Equals(p.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (existingOnSymbol.Count > 0)
                {
                    var signalDirection = signal.Direction == TradeDirection.Buy
                        ? PositionDirection.Long
                        : PositionDirection.Short;

                    bool isOpposite = existingOnSymbol.Any(p => p.Direction != signalDirection);
                    if (isOpposite)
                    {
                        decimal existingLots = existingOnSymbol.Sum(p => p.OpenLots);
                        decimal netLots = Math.Max(0, resolvedLot - existingLots);
                        requiredMargin = netLots * contractSize * signal.EntryPrice * quoteToAccountRate
                            / account.Leverage * slippageBuffer;
                    }
                }
            }

            if (requiredMargin > account.MarginAvailable)
                return Fail(
                    $"Insufficient margin: trade requires {requiredMargin:F2} {account.Currency} " +
                    $"but only {account.MarginAvailable:F2} available " +
                    $"(leverage {account.Leverage}:1, lot={resolvedLot}, " +
                    $"contract={contractSize:F0}, price={signal.EntryPrice}).");
        }

        // ── 19. Margin level safety buffer (configurable threshold + broker stop-out) ──
        if (account.MarginUsed > 0 && account.Equity > 0)
        {
            decimal additionalMargin = account.Leverage > 0
                ? resolvedLot * contractSize * signal.EntryPrice * quoteToAccountRate
                    / account.Leverage * slippageBuffer
                : 0;
            decimal projectedMarginUsed = account.MarginUsed + additionalMargin;

            // MT5 margin level includes credit: (Equity + Credit) / MarginUsed × 100
            decimal effectiveEquity = account.Equity + account.Credit;
            decimal projectedMarginLevel = effectiveEquity / projectedMarginUsed * 100m;

            // Use the higher of the engine's configured threshold and the broker's stop-out level
            // (with a safety buffer above broker stop-out to prevent getting too close)
            decimal effectiveThreshold = _options.MinMarginLevelPct;
            if (account.MarginSoStopOut > 0 &&
                string.Equals(account.MarginSoMode, "Percent", StringComparison.OrdinalIgnoreCase))
            {
                // Add a buffer above the broker's stop-out (e.g., stop-out at 50% → floor at 100%)
                decimal brokerFloor = account.MarginSoStopOut * _options.StopOutBufferMultiplier;
                effectiveThreshold = Math.Max(effectiveThreshold, brokerFloor);
            }

            if (projectedMarginLevel < effectiveThreshold)
                return Fail(
                    $"Projected margin level would be {projectedMarginLevel:F0}%, " +
                    $"below the safety threshold of {effectiveThreshold:F0}%" +
                    (effectiveThreshold > _options.MinMarginLevelPct
                        ? $" (broker stop-out at {account.MarginSoStopOut:F0}% × {_options.StopOutBufferMultiplier:F1} buffer)"
                        : "") +
                    $". Current: {effectiveEquity / account.MarginUsed * 100m:F0}%.");
        }

        // ── 20. Portfolio VaR gate (optional — requires IPortfolioRiskCalculator) ──
        if (_portfolioRisk is not null)
        {
            var marginalVaR = await _portfolioRisk.ComputeMarginalAsync(signal, context.Account, context.OpenPositions, cancellationToken);
            if (marginalVaR.WouldBreachLimit)
                return Fail($"Portfolio VaR95 would breach limit: post-trade VaR={marginalVaR.PostTradeVaR95:F2}, marginal={marginalVaR.MarginalVaR95:F2}");
        }

        // If the recovery cap rewrote the lot for THIS account, hand the resolved value
        // back so the caller can submit the order with it (and does not have to read the
        // signal, which remains at its original SuggestedLotSize for other accounts).
        // When resolvedLot == signal.SuggestedLotSize, leave ResolvedLotSize null so the
        // caller's `riskResult.ResolvedLotSize ?? signal.SuggestedLotSize` pattern keeps
        // its nice default-branch semantics.
        return resolvedLot == signal.SuggestedLotSize
            ? Pass()
            : new RiskCheckResult(true, null, resolvedLot);
    }

    public Task<RiskCheckResult> CheckDrawdownAsync(
        RiskProfile profile,
        decimal currentBalance,
        decimal peakBalance,
        decimal dailyStartBalance,
        decimal maxAbsoluteDailyLoss,
        CancellationToken cancellationToken)
    {
        if (currentBalance < 0)
            return Task.FromResult(Fail($"Current balance ({currentBalance}) is negative; trading halted."));

        // ── Minimum equity floor ──────────────────────────────────────────────
        if (profile.MinEquityFloor > 0 && currentBalance < profile.MinEquityFloor)
            return Task.FromResult(Fail(
                $"Current balance ({currentBalance:F2}) is below the minimum equity floor of " +
                $"{profile.MinEquityFloor:F2}. All trading is halted."));

        // ── Total drawdown (peak-to-trough) ─────────────────────────────────
        if (peakBalance > 0)
        {
            decimal totalDrawdownPct = (peakBalance - currentBalance) / peakBalance * 100m;

            if (totalDrawdownPct >= profile.MaxTotalDrawdownPct)
                return Task.FromResult(Fail(
                    $"Total drawdown of {totalDrawdownPct:F2}% has reached or exceeded the profile limit of {profile.MaxTotalDrawdownPct}%. " +
                    "Automated trading is paused until the account recovers."));
        }

        // ── Daily drawdown (from today's starting balance) ──────────────────
        if (dailyStartBalance > 0)
        {
            decimal dailyDrawdownPct = (dailyStartBalance - currentBalance) / dailyStartBalance * 100m;

            if (dailyDrawdownPct >= profile.MaxDailyDrawdownPct)
                return Task.FromResult(Fail(
                    $"Daily drawdown of {dailyDrawdownPct:F2}% has reached or exceeded the profile limit of {profile.MaxDailyDrawdownPct}%. " +
                    "No further orders will be placed today."));
        }

        // ── Absolute daily loss cap (account-level) ─────────────────────────
        if (maxAbsoluteDailyLoss > 0 && dailyStartBalance > 0)
        {
            decimal dailyLoss = dailyStartBalance - currentBalance;
            if (dailyLoss >= maxAbsoluteDailyLoss)
                return Task.FromResult(Fail(
                    $"Daily loss of {dailyLoss:F2} has reached or exceeded the absolute cap of " +
                    $"{maxAbsoluteDailyLoss:F2}. No further orders will be placed today."));
        }

        return Task.FromResult(Pass());
    }

    // NOTE: CheckDrawdownAsync is intentionally synchronous (no await) to avoid
    // unnecessary async state machine overhead for pure-computation checks.
    // It returns Task<> to match the IRiskChecker interface contract.

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the pip size for the given symbol specification.
    /// Uses the explicit <see cref="CurrencyPair.PipSize"/> if set, otherwise
    /// derives from <see cref="CurrencyPair.DecimalPlaces"/>.
    /// </summary>
    private static decimal GetPipSize(CurrencyPair spec)
    {
        if (spec.PipSize > 0)
            return spec.PipSize;

        return spec.DecimalPlaces > 0
            ? (decimal)Math.Pow(10, -(spec.DecimalPlaces - 1))
            : 0.0001m;
    }

    /// <summary>
    /// Counts positions accounting for margin mode. On netting accounts, multiple
    /// positions on the same symbol in the same direction count as one effective position.
    /// </summary>
    private static int CountEffectivePositions(IReadOnlyList<Position> positions, MarginMode marginMode)
    {
        if (marginMode == MarginMode.Netting)
            return positions.Select(p => p.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        return positions.Count;
    }

    /// <summary>
    /// Counts open positions correlated with the given symbol using a tiered approach:
    /// <list type="number">
    ///   <item>Config-based groups (<see cref="CorrelationGroupOptions"/>): if the symbol is in
    ///         a configured group, count all open positions in that same group.</item>
    ///   <item>Computed correlations (<see cref="RiskCheckContext.ComputedCorrelations"/>): if the
    ///         CorrelationMatrixWorker has computed rolling correlations, count positions whose
    ///         |correlation| with the signal symbol exceeds <see cref="CorrelationThreshold"/>.</item>
    ///   <item>Fallback: currency-code matching (first/last 3 chars overlap).</item>
    /// </list>
    /// </summary>
    private decimal CorrelationThreshold => _options.CorrelationThreshold;

    private int CountCorrelatedPositions(
        IReadOnlyList<Position> positions,
        string symbol,
        string[][] configGroups,
        IReadOnlyDictionary<string, decimal>? computedCorrelations)
    {
        var upperSymbol = symbol.ToUpperInvariant();

        // Tier 1: Config-based correlation groups
        var configGroup = FindConfigGroup(upperSymbol, configGroups);
        if (configGroup is not null)
        {
            return positions.Count(p =>
                configGroup.Contains(p.Symbol.ToUpperInvariant()));
        }

        // Tier 2: Computed rolling correlations from CorrelationMatrixWorker
        if (computedCorrelations is not null && computedCorrelations.Count > 0)
        {
            return positions.Count(p =>
            {
                // Look up the correlation pair key (alphabetically ordered)
                string key = BuildCorrelationKey(upperSymbol, p.Symbol.ToUpperInvariant());
                return computedCorrelations.TryGetValue(key, out decimal corr)
                    && Math.Abs(corr) >= CorrelationThreshold;
            });
        }

        // Tier 3: Fallback — currency-code matching (base/quote overlap)
        if (upperSymbol.Length < 6) return 0;
        string baseCcy = upperSymbol[..3];
        string quoteCcy = upperSymbol[3..6];

        return positions.Count(p =>
        {
            if (p.Symbol.Length < 6) return false;
            string pUpper = p.Symbol.ToUpperInvariant();
            string pBase = pUpper[..3];
            string pQuote = pUpper[3..6];
            return pBase == baseCcy || pBase == quoteCcy || pQuote == baseCcy || pQuote == quoteCcy;
        });
    }

    private static string[]? FindConfigGroup(string symbol, string[][] groups)
    {
        foreach (var group in groups)
        {
            if (Array.Exists(group, s => string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase)))
                return group;
        }
        return null;
    }

    /// <summary>Builds an alphabetically-ordered pair key for correlation lookup.</summary>
    internal static string BuildCorrelationKey(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    /// <summary>
    /// Calculates the lot-based exposure for a symbol, respecting margin mode.
    /// <list type="bullet">
    ///   <item><description>Hedging: gross lots (long + short) — both sides consume margin independently.</description></item>
    ///   <item><description>Netting: net lots in the signal direction — opposite positions offset each other.</description></item>
    /// </list>
    /// </summary>
    private static decimal CalculateSymbolExposureLots(
        IReadOnlyList<Position> positions, string symbol, TradeDirection signalDirection, MarginMode marginMode)
    {
        var symbolPositions = positions
            .Where(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

        if (marginMode == MarginMode.Netting)
        {
            decimal longLots = symbolPositions
                .Where(p => p.Direction == PositionDirection.Long)
                .Sum(p => p.OpenLots);
            decimal shortLots = symbolPositions
                .Where(p => p.Direction == PositionDirection.Short)
                .Sum(p => p.OpenLots);

            // Net lots in the direction of the new signal
            return signalDirection == TradeDirection.Buy
                ? Math.Max(0, longLots - shortLots)
                : Math.Max(0, shortLots - longLots);
        }

        // Hedging: gross lots — every position uses margin regardless of direction
        return symbolPositions.Sum(p => p.OpenLots);
    }

    /// <summary>
    /// Calculates the approximate total margin used across all open positions.
    /// Uses per-symbol contract sizes and quote-to-account conversion rates from context
    /// when available, falling back to 100k contract size and 1.0 rate for unknowns.
    /// </summary>
    private static decimal CalculateTotalPortfolioMargin(
        IReadOnlyList<Position> positions,
        decimal leverage,
        IReadOnlyDictionary<string, decimal>? contractSizes,
        IReadOnlyDictionary<string, decimal>? quoteToAccountRates)
    {
        if (leverage <= 0) return 0;

        const decimal fallbackContractSize = 100_000m;
        return positions.Sum(p =>
        {
            decimal cs = fallbackContractSize;
            if (contractSizes is not null &&
                contractSizes.TryGetValue(p.Symbol, out decimal mapped))
            {
                cs = mapped;
            }

            decimal rate = 1.0m;
            if (quoteToAccountRates is not null &&
                quoteToAccountRates.TryGetValue(p.Symbol, out decimal mappedRate))
            {
                rate = mappedRate;
            }

            return p.OpenLots * cs * p.AverageEntryPrice * rate / leverage;
        });
    }

    /// <summary>
    /// Returns the gap risk multiplier if the current time is within a gap risk window.
    /// Gap risk windows include:
    /// <list type="bullet">
    ///   <item><description>Friday before market close (configurable window)</description></item>
    ///   <item><description>Saturday/Sunday (market closed)</description></item>
    ///   <item><description>Trading day before a configured market holiday</description></item>
    ///   <item><description>On the holiday itself</description></item>
    /// </list>
    /// Returns 1.0 outside any gap window.
    /// </summary>
    private decimal GetGapRiskMultiplier(decimal profileMultiplier)
    {
        if (profileMultiplier <= 1.0m || _options.WeekendGapWindowHours <= 0)
            return 1.0m;

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Saturday/Sunday — market is closed, any pending execution carries full gap risk
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return profileMultiplier;

        // Friday market close is typically 22:00 UTC (17:00 EST)
        if (now.DayOfWeek == DayOfWeek.Friday)
        {
            var fridayClose = now.Date.AddHours(22);
            var windowStart = fridayClose.AddHours(-_options.WeekendGapWindowHours);

            if (now >= windowStart && now <= fridayClose)
                return profileMultiplier;
        }

        // Holiday check — apply multiplier on the day before and on the holiday itself
        if (_options.MarketHolidays is { Count: > 0 })
        {
            var today = now.Date;
            var tomorrow = today.AddDays(1);

            foreach (var holiday in _options.MarketHolidays)
            {
                if (TryParseHoliday(holiday, today.Year, out var holidayDate))
                {
                    // On the holiday itself
                    if (today == holidayDate)
                        return profileMultiplier;

                    // Within the gap window before the holiday
                    if (tomorrow == holidayDate)
                    {
                        var holidayClose = today.AddHours(22); // assume market closes at 22:00 UTC day before
                        var windowStart = holidayClose.AddHours(-_options.WeekendGapWindowHours);
                        if (now >= windowStart)
                            return profileMultiplier;
                    }
                }

                // Also check next year for holidays near year boundary (e.g., Jan 1 checked in late Dec)
                if (TryParseHoliday(holiday, today.Year + 1, out var nextYearHolidayDate))
                {
                    if (tomorrow == nextYearHolidayDate)
                    {
                        var holidayClose = today.AddHours(22);
                        var windowStart = holidayClose.AddHours(-_options.WeekendGapWindowHours);
                        if (now >= windowStart)
                            return profileMultiplier;
                    }
                }
            }
        }

        return 1.0m;
    }

    private bool TryParseHoliday(string mmDd, int year, out DateTime result)
    {
        result = default;
        var parts = mmDd.Split('-');
        if (parts.Length != 2)
        {
            _logger.LogWarning("RiskChecker: invalid holiday format '{Holiday}' — expected 'MM-dd'", mmDd);
            return false;
        }

        if (int.TryParse(parts[0], out int month) && int.TryParse(parts[1], out int day) &&
            month >= 1 && month <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
        {
            try
            {
                result = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                _logger.LogWarning("RiskChecker: invalid holiday date '{Holiday}' for year {Year}", mmDd, year);
                return false;
            }
        }

        _logger.LogWarning("RiskChecker: invalid holiday '{Holiday}' — month/day out of range", mmDd);
        return false;
    }

    /// <summary>
    /// Reads the current drawdown recovery mode from the <c>DrawdownRecovery:ActiveMode</c>
    /// engine config key. Returns "Normal" if not set or not found.
    /// </summary>
    private async Task<string> GetRecoveryModeAsync(CancellationToken ct)
    {
        var config = await _readDb.GetDbContext()
            .Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == "DrawdownRecovery:ActiveMode" && !c.IsDeleted, ct);
        return config?.Value ?? "Normal";
    }

    /// <summary>
    /// Reads the reduced-mode lot multiplier from the <c>DrawdownRecovery:ReducedLotMultiplier</c>
    /// engine config key. Returns 0.5 (50%) if not set or unparseable.
    /// </summary>
    private async Task<decimal> GetRecoveryMultiplierAsync(CancellationToken ct)
    {
        const decimal defaultMultiplier = 0.5m;
        var config = await _readDb.GetDbContext()
            .Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == "DrawdownRecovery:ReducedLotMultiplier" && !c.IsDeleted, ct);

        if (config?.Value is not null && decimal.TryParse(config.Value, out decimal multiplier))
            return Math.Clamp(multiplier, 0.01m, 1.0m);

        return defaultMultiplier;
    }

    private static RiskCheckResult Fail(string reason) =>
        new(Passed: false, BlockReason: reason);

    private static RiskCheckResult Pass() =>
        new(Passed: true, BlockReason: null);
}
