using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.RiskProfiles.Services.Steps;

/// <summary>
/// Checks daily drawdown percentage and consecutive loss streak limits.
/// </summary>
public sealed class DrawdownRiskCheckStep : IRiskCheckStep
{
    public string Name => "Drawdown";

    public Task<RiskCheckStepResult> CheckAsync(TradeSignal signal, RiskCheckContext context, CancellationToken ct)
    {
        if (context.Profile is null || context.DailyStartBalance <= 0)
            return Task.FromResult(new RiskCheckStepResult(true));

        decimal equity = context.Account?.Equity > 0 ? context.Account.Equity : context.Account?.Balance ?? 0;
        if (equity <= 0)
            return Task.FromResult(new RiskCheckStepResult(true));

        // Daily drawdown gate — compare as percentage * 100 to match RiskProfile convention
        decimal dailyDrawdownPct = (context.DailyStartBalance - equity) / context.DailyStartBalance * 100m;
        if (context.Profile.MaxDailyDrawdownPct > 0 && dailyDrawdownPct >= context.Profile.MaxDailyDrawdownPct)
            return Task.FromResult(new RiskCheckStepResult(false,
                $"Daily drawdown {dailyDrawdownPct:F2}% exceeds max {context.Profile.MaxDailyDrawdownPct:F2}%"));

        // Consecutive loss cooldown
        if (context.Profile.MaxConsecutiveLosses > 0 && context.ConsecutiveLosses >= context.Profile.MaxConsecutiveLosses)
            return Task.FromResult(new RiskCheckStepResult(false,
                $"Consecutive losses ({context.ConsecutiveLosses}) reached max ({context.Profile.MaxConsecutiveLosses})"));

        return Task.FromResult(new RiskCheckStepResult(true));
    }
}
