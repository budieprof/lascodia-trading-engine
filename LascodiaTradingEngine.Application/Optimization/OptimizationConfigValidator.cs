using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Validates hot-reloaded optimization configuration for sanity. Catches operator errors
/// that could silently block all auto-approvals or waste compute indefinitely.
/// </summary>
internal static class OptimizationConfigValidator
{
    /// <summary>
    /// Returns a list of issues found. Empty list means config is valid.
    /// Auto-corrects dangerously invalid values and logs warnings for suspicious-but-valid combos.
    /// </summary>
    internal static List<string> Validate(
        decimal autoApprovalImprovementThreshold,
        decimal autoApprovalMinHealthScore,
        decimal minBootstrapCILower,
        double embargoRatio,
        int tpeBudget,
        int tpeInitialSamples,
        int maxParallelBacktests,
        int screeningTimeoutSeconds,
        double correlationParamThreshold,
        double sensitivityPerturbPct,
        int gpEarlyStopPatience,
        int cooldownDays,
        int checkpointEveryN,
        ILogger logger,
        double sensitivityDegradationTolerance = 0.20,
        double walkForwardMinMaxRatio = 0.50,
        double costStressMultiplier = 2.0,
        int cpcvNFolds = 6,
        int cpcvTestFoldCount = 2,
        int minOosCandlesForValidation = 50,
        int circuitBreakerThreshold = 10,
        int minCandidateTrades = 10,
        string successiveHalvingRungs = "0.25,0.50",
        double regimeBlendRatio = 0.20,
        double minEquityCurveR2 = 0.60,
        double maxTradeTimeConcentration = 0.60)
    {
        var issues = new List<string>();

        // Hard errors: these would silently break the system
        if (minBootstrapCILower >= autoApprovalMinHealthScore)
        {
            issues.Add($"MinBootstrapCILower ({minBootstrapCILower}) >= AutoApprovalMinHealthScore ({autoApprovalMinHealthScore}) — " +
                       "CI lower bound can never exceed the absolute score, making auto-approval impossible");
        }

        if (autoApprovalImprovementThreshold < 0m || autoApprovalImprovementThreshold > 1m)
            issues.Add($"AutoApprovalImprovementThreshold ({autoApprovalImprovementThreshold}) outside [0, 1]");

        if (embargoRatio < 0 || embargoRatio > 0.3)
            issues.Add($"EmbargoRatio ({embargoRatio}) outside [0, 0.3] — would discard too much data");

        if (tpeInitialSamples >= tpeBudget)
            issues.Add($"TpeInitialSamples ({tpeInitialSamples}) >= TpeBudget ({tpeBudget}) — no room for surrogate-guided search");

        if (maxParallelBacktests < 1)
            issues.Add($"MaxParallelBacktests ({maxParallelBacktests}) < 1");

        if (screeningTimeoutSeconds < 5)
            issues.Add($"ScreeningTimeoutSeconds ({screeningTimeoutSeconds}) < 5 — backtests will time out immediately");

        if (correlationParamThreshold <= 0 || correlationParamThreshold > 1)
            issues.Add($"CorrelationParamThreshold ({correlationParamThreshold}) outside (0, 1]");

        if (sensitivityPerturbPct <= 0 || sensitivityPerturbPct > 0.5)
            issues.Add($"SensitivityPerturbPct ({sensitivityPerturbPct}) outside (0, 0.5]");

        if (sensitivityDegradationTolerance <= 0 || sensitivityDegradationTolerance > 1)
            issues.Add($"SensitivityDegradationTolerance ({sensitivityDegradationTolerance}) outside (0, 1]");

        if (walkForwardMinMaxRatio <= 0 || walkForwardMinMaxRatio > 1)
            issues.Add($"WalkForwardMinMaxRatio ({walkForwardMinMaxRatio}) outside (0, 1]");

        if (costStressMultiplier < 1.0)
            issues.Add($"CostStressMultiplier ({costStressMultiplier}) < 1.0 — must be at least 1x");

        if (regimeBlendRatio < 0 || regimeBlendRatio > 1)
            issues.Add($"RegimeBlendRatio ({regimeBlendRatio}) outside [0, 1]");

        if (minEquityCurveR2 < 0 || minEquityCurveR2 > 1)
            issues.Add($"MinEquityCurveR2 ({minEquityCurveR2}) outside [0, 1]");

        if (maxTradeTimeConcentration < 0 || maxTradeTimeConcentration > 1)
            issues.Add($"MaxTradeTimeConcentration ({maxTradeTimeConcentration}) outside [0, 1]");

        if (gpEarlyStopPatience < 1)
            issues.Add($"GpEarlyStopPatience ({gpEarlyStopPatience}) < 1");

        if (gpEarlyStopPatience > 0 && gpEarlyStopPatience * maxParallelBacktests > tpeBudget)
            logger.LogWarning(
                "OptimizationConfigValidator: GpEarlyStopPatience={Patience} × MaxParallelBacktests={Parallel} > TpeBudget={Budget} — early stopping may never fire",
                gpEarlyStopPatience, maxParallelBacktests, tpeBudget);

        if (correlationParamThreshold > 0.95)
            logger.LogWarning(
                "OptimizationConfigValidator: CorrelationParamThreshold={Threshold} is very high — almost no parameter correlation will be flagged",
                correlationParamThreshold);

        // Warnings: suspicious but technically valid
        if (cooldownDays < 3)
            logger.LogWarning("OptimizationConfigValidator: CooldownDays={Days} is very low — strategies may be re-optimised before results stabilise", cooldownDays);

        if (tpeBudget < 20)
            logger.LogWarning("OptimizationConfigValidator: TpeBudget={Budget} is low — surrogate may not converge", tpeBudget);

        if (checkpointEveryN > tpeBudget)
            logger.LogWarning("OptimizationConfigValidator: CheckpointEveryN={N} > TpeBudget={Budget} — no checkpoints will fire", checkpointEveryN, tpeBudget);

        if (autoApprovalMinHealthScore > 0.90m)
            logger.LogWarning("OptimizationConfigValidator: AutoApprovalMinHealthScore={Score} is very high — auto-approval may rarely succeed", autoApprovalMinHealthScore);

        // Cross-constraint validation: these catch multi-parameter conflicts
        if (cpcvNFolds <= cpcvTestFoldCount)
            issues.Add($"CpcvNFolds ({cpcvNFolds}) must be > CpcvTestFoldCount ({cpcvTestFoldCount}) — " +
                       "no training folds would remain");

        if (circuitBreakerThreshold > 0 && circuitBreakerThreshold <= maxParallelBacktests)
            logger.LogWarning(
                "OptimizationConfigValidator: CircuitBreakerThreshold={Threshold} <= MaxParallelBacktests={Parallel} — " +
                "circuit breaker may trip from a single batch of parallel failures rather than a sustained pattern",
                circuitBreakerThreshold, maxParallelBacktests);

        if (minOosCandlesForValidation > 0 && embargoRatio > 0)
        {
            // With a typical 6-month lookback of ~130 D1 candles, 70% train ratio, and 5% embargo:
            // OOS = 130 - 91(train) - 7(embargo) = 32 candles < minOosCandlesForValidation(50).
            // Warn when the combo is likely to trigger synthetic CI fallback most of the time.
            double effectiveOosRatio = 1.0 - 0.70 - embargoRatio; // approximate with default train ratio
            if (effectiveOosRatio > 0)
            {
                int estimatedMinCandlesNeeded = (int)(minOosCandlesForValidation / effectiveOosRatio);
                if (estimatedMinCandlesNeeded > 500)
                    logger.LogWarning(
                        "OptimizationConfigValidator: MinOosCandlesForValidation={MinOos} with EmbargoRatio={Embargo:F2} " +
                        "requires ~{MinCandles}+ total candles — higher-timeframe strategies may always use synthetic CI",
                        minOosCandlesForValidation, embargoRatio, estimatedMinCandlesNeeded);
            }
        }

        if (minCandidateTrades > 0 && minOosCandlesForValidation > 0 && minCandidateTrades > minOosCandlesForValidation)
            logger.LogWarning(
                "OptimizationConfigValidator: MinCandidateTrades={Trades} > MinOosCandlesForValidation={OosCandles} — " +
                "impossible to generate enough trades from fewer OOS candles",
                minCandidateTrades, minOosCandlesForValidation);

        // Validate SuccessiveHalvingRungs format at config time rather than failing mid-search
        if (!string.IsNullOrWhiteSpace(successiveHalvingRungs))
        {
            var rungParts = successiveHalvingRungs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int validRungs = 0;
            foreach (var part in rungParts)
            {
                if (double.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out double val) && val > 0 && val < 1.0)
                    validRungs++;
            }
            if (validRungs == 0 && rungParts.Length > 0)
                issues.Add($"SuccessiveHalvingRungs (\"{successiveHalvingRungs}\") contains no valid fidelity levels — " +
                           "values must be between 0 and 1 exclusive (e.g. \"0.25,0.50\")");
        }

        return issues;
    }
}
