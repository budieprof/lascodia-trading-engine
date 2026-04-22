using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Result of a risk check. <see cref="Passed"/> is <c>true</c> if the trade is allowed;
/// otherwise <see cref="BlockReason"/> describes why it was rejected.
///
/// <para><see cref="ResolvedLotSize"/> is populated when the checker has rewritten the lot
/// for this particular account (e.g. drawdown recovery cap). Callers MUST use this value
/// when submitting the order rather than <c>signal.SuggestedLotSize</c>, because the
/// signal may be evaluated against other accounts later and mutating it would bleed one
/// account's recovery mode into another's execution. <c>null</c> means "use the signal's
/// original lot unchanged".</para>
/// </summary>
public record RiskCheckResult(bool Passed, string? BlockReason, decimal? ResolvedLotSize = null);

/// <summary>
/// Context object passed to the risk checker containing the account state and open positions
/// needed to evaluate margin, exposure, and position-count constraints.
/// </summary>
public record RiskCheckContext
{
    public required TradingAccount Account { get; init; }
    public required RiskProfile Profile { get; init; }
    public required IReadOnlyList<Position> OpenPositions { get; init; }
    public required CurrencyPair? SymbolSpec { get; init; }
    public int TradesToday { get; init; }
    public bool IsInRecoveryMode { get; init; }

    /// <summary>
    /// Number of consecutive losing trades (most recent streak).
    /// Used by the consecutive loss gate to pause trading after a losing streak.
    /// </summary>
    public int ConsecutiveLosses { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent losing trade in the current streak.
    /// Used by the consecutive loss gate to auto-reset after a configurable cooldown.
    /// Null if no recent losses or if the data is unavailable.
    /// </summary>
    public DateTime? LastLossAt { get; init; }

    /// <summary>
    /// Current bid/ask spread in price units for the signal's symbol.
    /// Null if live spread data is unavailable.
    /// </summary>
    public decimal? CurrentSpread { get; init; }

    /// <summary>
    /// Today's starting balance (balance at 00:00 UTC).
    /// Used to calculate daily drawdown independently from total (peak-to-trough) drawdown.
    /// </summary>
    public decimal DailyStartBalance { get; init; }

    /// <summary>
    /// Map of symbol → contract size for all symbols with open positions.
    /// Used by the portfolio-level margin calculation to avoid the 100k fallback.
    /// </summary>
    public IReadOnlyDictionary<string, decimal>? PortfolioContractSizes { get; init; }

    /// <summary>
    /// Exchange rate to convert from the signal symbol's quote currency to the account currency.
    /// For EURUSD on a USD account, this is 1.0 (quote is already USD).
    /// For EURGBP on a USD account, this is the current GBPUSD rate.
    /// Null means assume 1.0 (same currency or rate unavailable).
    /// </summary>
    public decimal? QuoteToAccountRate { get; init; }

    /// <summary>
    /// Map of symbol → quote-to-account exchange rate for all symbols with open positions.
    /// Used by the portfolio-level margin calculation for cross-currency positions.
    /// Null means assume 1.0 for all positions.
    /// </summary>
    public IReadOnlyDictionary<string, decimal>? PortfolioQuoteToAccountRates { get; init; }

    /// <summary>
    /// Computed rolling correlation coefficients between symbol pairs.
    /// Keys are alphabetically-ordered pairs: "EURUSD|GBPUSD" → 0.85.
    /// Populated by the CorrelationMatrixWorker. Null if no computed data available.
    /// </summary>
    public IReadOnlyDictionary<string, decimal>? ComputedCorrelations { get; init; }
}

public interface IRiskChecker
{
    /// <summary>
    /// Tier 2 account-level risk check — validates a trade signal against a specific
    /// trading account's state (margin, exposure, position limits, drawdown, lot constraints).
    /// Signal-level (Tier 1) validation should be performed by <see cref="ISignalValidator"/>
    /// before invoking this method.
    /// </summary>
    Task<RiskCheckResult> CheckAsync(
        TradeSignal signal,
        RiskCheckContext context,
        CancellationToken cancellationToken);

    /// <summary>Checks account-level drawdown rules (daily and total).</summary>
    Task<RiskCheckResult> CheckDrawdownAsync(
        RiskProfile profile,
        decimal currentBalance,
        decimal peakBalance,
        decimal dailyStartBalance,
        decimal maxAbsoluteDailyLoss,
        CancellationToken cancellationToken);
}
