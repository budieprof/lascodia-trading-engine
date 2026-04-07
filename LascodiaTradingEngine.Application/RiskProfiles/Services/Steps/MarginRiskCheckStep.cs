using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.RiskProfiles.Services.Steps;

/// <summary>
/// Checks whether the trading account has sufficient free margin to open the proposed trade.
/// Uses the account's leverage, the symbol's contract size, and the quote-to-account
/// conversion rate to compute the required margin.
/// </summary>
public sealed class MarginRiskCheckStep : IRiskCheckStep
{
    public string Name => "Margin";

    public Task<RiskCheckStepResult> CheckAsync(TradeSignal signal, RiskCheckContext context, CancellationToken ct)
    {
        if (context.Account is null || context.Profile is null)
            return Task.FromResult(new RiskCheckStepResult(true));

        decimal equity = context.Account.Equity > 0 ? context.Account.Equity : context.Account.Balance;
        if (equity <= 0)
            return Task.FromResult(new RiskCheckStepResult(false, "Account equity is zero or negative"));

        decimal contractSize = context.SymbolSpec?.ContractSize ?? 100_000m;
        decimal notional = signal.SuggestedLotSize * contractSize * signal.EntryPrice;
        decimal conversionRate = context.QuoteToAccountRate ?? 1m;
        decimal leverage = context.Account.Leverage > 0 ? context.Account.Leverage : 100m;
        decimal requiredMargin = notional * conversionRate / leverage;

        decimal freeMargin = context.Account.MarginAvailable;
        if (requiredMargin > freeMargin)
            return Task.FromResult(new RiskCheckStepResult(false,
                $"Insufficient margin: required {requiredMargin:F2}, available {freeMargin:F2}"));

        return Task.FromResult(new RiskCheckStepResult(true));
    }
}
