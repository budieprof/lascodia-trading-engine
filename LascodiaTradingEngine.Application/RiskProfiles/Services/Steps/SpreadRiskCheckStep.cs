using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.RiskProfiles.Services.Steps;

/// <summary>
/// Checks whether the current bid/ask spread exceeds the configured maximum.
/// Uses the symbol's pip size to convert the raw spread into pips before comparison.
/// </summary>
public sealed class SpreadRiskCheckStep : IRiskCheckStep
{
    private readonly decimal _maxSpreadPips;

    /// <summary>
    /// Creates a new spread risk check step.
    /// </summary>
    /// <param name="maxSpreadPips">
    /// Maximum allowed spread in pips. Zero or negative disables the check.
    /// This value typically comes from <c>RiskCheckerOptions.MaxSpreadPips</c>.
    /// </param>
    public SpreadRiskCheckStep(decimal maxSpreadPips)
    {
        _maxSpreadPips = maxSpreadPips;
    }

    public string Name => "Spread";

    public Task<RiskCheckStepResult> CheckAsync(TradeSignal signal, RiskCheckContext context, CancellationToken ct)
    {
        if (_maxSpreadPips <= 0 || context.CurrentSpread is null || context.SymbolSpec is null)
            return Task.FromResult(new RiskCheckStepResult(true));

        // Reject inverted spreads (data quality issue)
        if (context.CurrentSpread.Value < 0)
            return Task.FromResult(new RiskCheckStepResult(false,
                $"Inverted quote detected for {signal.Symbol} (spread={context.CurrentSpread.Value:F5})"));

        decimal pipSize = GetPipSize(context.SymbolSpec);
        decimal spreadPips = pipSize > 0 ? context.CurrentSpread.Value / pipSize : 0;

        if (spreadPips > _maxSpreadPips)
            return Task.FromResult(new RiskCheckStepResult(false,
                $"Spread {spreadPips:F1} pips exceeds max {_maxSpreadPips:F1} pips for {signal.Symbol}"));

        return Task.FromResult(new RiskCheckStepResult(true));
    }

    private static decimal GetPipSize(CurrencyPair spec)
    {
        if (spec.PipSize > 0)
            return spec.PipSize;

        return spec.DecimalPlaces > 0
            ? (decimal)Math.Pow(10, -(spec.DecimalPlaces - 1))
            : 0.0001m;
    }
}
