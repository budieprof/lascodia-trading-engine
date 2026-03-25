using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Context for signal-level (Tier 1) validation. Contains only data needed to assess
/// signal quality — no account state, no open positions.
/// </summary>
public record SignalValidationContext
{
    public required RiskProfile Profile { get; init; }
    public required CurrencyPair? SymbolSpec { get; init; }
}

/// <summary>
/// Validates a trade signal's intrinsic quality before it is approved for consumption by
/// any trading account. This is Tier 1 of the two-tier risk checking architecture:
/// <list type="bullet">
///   <item><description>Tier 1 (<see cref="ISignalValidator"/>): account-agnostic signal validation (expiry, SL/TP, R:R, ML agreement)</description></item>
///   <item><description>Tier 2 (<see cref="IRiskChecker"/>): account-specific risk checks (margin, exposure, drawdown, position limits)</description></item>
/// </list>
/// </summary>
public interface ISignalValidator
{
    Task<RiskCheckResult> ValidateAsync(
        TradeSignal signal,
        SignalValidationContext context,
        CancellationToken cancellationToken);
}
