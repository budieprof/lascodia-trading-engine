using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

public record RiskCheckResult(bool Passed, string? BlockReason);

public interface IRiskChecker
{
    /// <summary>Validates a trade signal against risk profile rules before order creation.</summary>
    Task<RiskCheckResult> CheckAsync(TradeSignal signal, RiskProfile profile, CancellationToken cancellationToken);

    /// <summary>Checks account-level drawdown rules (daily and total).</summary>
    Task<RiskCheckResult> CheckDrawdownAsync(RiskProfile profile, decimal currentBalance, decimal peakBalance, CancellationToken cancellationToken);
}
