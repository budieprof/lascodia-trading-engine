using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.RiskProfiles.Services;

/// <summary>
/// Composite risk checker that runs all registered <see cref="IRiskCheckStep"/> implementations
/// before delegating to the monolithic <see cref="RiskChecker"/>. Each step can independently
/// block a signal with a specific reason. If all steps pass, the full RiskChecker runs.
/// </summary>
public sealed class RiskCheckerPipeline : IRiskChecker
{
    private readonly IEnumerable<IRiskCheckStep> _steps;
    private readonly RiskChecker _innerChecker;
    private readonly ILogger<RiskCheckerPipeline> _logger;

    public RiskCheckerPipeline(
        IEnumerable<IRiskCheckStep> steps,
        RiskChecker innerChecker,
        ILogger<RiskCheckerPipeline> logger)
    {
        _steps = steps;
        _innerChecker = innerChecker;
        _logger = logger;
    }

    public async Task<RiskCheckResult> CheckAsync(TradeSignal signal, RiskCheckContext context, CancellationToken ct)
    {
        foreach (var step in _steps)
        {
            try
            {
                var result = await step.CheckAsync(signal, context, ct);
                if (!result.Passed)
                {
                    _logger.LogInformation("RiskCheckerPipeline: step {Step} blocked signal {SignalId} — {Reason}",
                        step.Name, signal.Id, result.BlockReason);
                    return new RiskCheckResult(Passed: false, BlockReason: result.BlockReason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "RiskCheckerPipeline: step {Step} threw for signal {SignalId} — BLOCKING signal (fail-closed)",
                    step.Name, signal.Id);
                return new RiskCheckResult(
                    Passed: false,
                    BlockReason: $"Risk check step '{step.Name}' failed with error: {ex.Message}");
            }
        }

        return await _innerChecker.CheckAsync(signal, context, ct);
    }

    public Task<RiskCheckResult> CheckDrawdownAsync(
        RiskProfile profile,
        decimal currentBalance,
        decimal peakBalance,
        decimal dailyStartBalance,
        decimal maxAbsoluteDailyLoss,
        CancellationToken cancellationToken)
    {
        // Drawdown checks are account-level and don't go through the step pipeline.
        return _innerChecker.CheckDrawdownAsync(
            profile, currentBalance, peakBalance, dailyStartBalance, maxAbsoluteDailyLoss, cancellationToken);
    }
}
