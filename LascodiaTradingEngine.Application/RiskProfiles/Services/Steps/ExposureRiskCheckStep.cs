using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.RiskProfiles.Services.Steps;

/// <summary>
/// Checks position-count exposure limits: max open positions globally and per symbol.
/// </summary>
public sealed class ExposureRiskCheckStep : IRiskCheckStep
{
    public string Name => "Exposure";

    public Task<RiskCheckStepResult> CheckAsync(TradeSignal signal, RiskCheckContext context, CancellationToken ct)
    {
        if (context.Profile is null)
            return Task.FromResult(new RiskCheckStepResult(true));

        // Max open positions check
        int openCount = context.OpenPositions?.Count ?? 0;
        if (context.Profile.MaxOpenPositions > 0 && openCount >= context.Profile.MaxOpenPositions)
            return Task.FromResult(new RiskCheckStepResult(false,
                $"Max open positions ({context.Profile.MaxOpenPositions}) reached"));

        // Per-symbol position limit
        int symbolPositions = context.OpenPositions?
            .Count(p => string.Equals(p.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase)) ?? 0;
        if (context.Profile.MaxPositionsPerSymbol > 0 && symbolPositions >= context.Profile.MaxPositionsPerSymbol)
            return Task.FromResult(new RiskCheckStepResult(false,
                $"Max positions per symbol ({context.Profile.MaxPositionsPerSymbol}) reached for {signal.Symbol}"));

        return Task.FromResult(new RiskCheckStepResult(true));
    }
}
