namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Central approval policy for optimization winners. Keeps approval logic deterministic,
/// structured, and independently testable.
/// </summary>
internal static class OptimizationApprovalPolicy
{
    internal sealed record Input(
        decimal CandidateImprovement,
        decimal OosHealthScore,
        int TotalTrades,
        decimal SharpeRatio,
        decimal MaxDrawdownPct,
        decimal WinRate,
        decimal ProfitFactor,
        decimal CILower,
        decimal MinBootstrapCILower,
        bool DegradationFailed,
        bool WfStable,
        bool MtfCompatible,
        bool CorrelationSafe,
        bool SensitivityOk,
        bool CostSensitiveOk,
        bool TemporalCorrelationSafe,
        bool PortfolioCorrelationSafe,
        bool PermSignificant,
        bool CvConsistent,
        double TemporalMaxOverlap,
        double PortfolioMaxCorrelation,
        double PermPValue,
        double PermCorrectedAlpha,
        double CvValue,
        decimal PessimisticScore,
        string SensitivityReport,
        decimal AutoApprovalImprovementThreshold,
        decimal AutoApprovalMinHealthScore,
        int MinCandidateTrades,
        double MaxCvCoefficientOfVariation,
        bool KellySizingOk,
        double KellySharpeRatio,
        double FixedLotSharpeRatio,
        bool EquityCurveOk,
        bool TimeConcentrationOk,
        double AssetClassSharpeMultiplier = 1.0,
        double AssetClassPfMultiplier = 1.0,
        double AssetClassDrawdownMultiplier = 1.0,
        bool GenesisRegressionOk = true,
        bool HasSufficientOutOfSampleData = true,
        double TailRiskVaR99 = 0,
        bool TailRiskWithinThreshold = true);

    internal sealed record Result(
        bool Passed,
        bool CompositeGateOk,
        bool MultiObjectiveGateOk,
        bool SafetyGatesOk,
        string FailureReason,
        IReadOnlyDictionary<string, object> StructuredReport);

    internal static Result Evaluate(Input input)
    {
        bool compositeGateOk = input.CandidateImprovement >= input.AutoApprovalImprovementThreshold
            && input.OosHealthScore >= input.AutoApprovalMinHealthScore;

        bool multiObjectiveGateOk = false;
        if (!compositeGateOk && input.TotalTrades >= input.MinCandidateTrades)
        {
            bool strongSharpe  = (double)input.SharpeRatio >= 1.0 * input.AssetClassSharpeMultiplier;
            bool lowDrawdown   = (double)input.MaxDrawdownPct <= 10.0 / input.AssetClassDrawdownMultiplier;
            bool decentWinRate = (double)input.WinRate >= 0.45;
            bool decentPF      = (double)input.ProfitFactor >= 1.2 * input.AssetClassPfMultiplier;

            int strongMetrics = (strongSharpe ? 1 : 0) + (lowDrawdown ? 1 : 0) +
                                (decentWinRate ? 1 : 0) + (decentPF ? 1 : 0);

            multiObjectiveGateOk = strongMetrics >= 3
                && input.OosHealthScore >= input.AutoApprovalMinHealthScore * 0.80m;
        }

        bool safetyGatesOk = input.CILower >= input.MinBootstrapCILower
            && input.HasSufficientOutOfSampleData
            && !input.DegradationFailed
            && input.WfStable
            && input.MtfCompatible
            && input.CorrelationSafe
            && input.SensitivityOk
            && input.CostSensitiveOk
            && input.TemporalCorrelationSafe
            && input.PortfolioCorrelationSafe
            && input.PermSignificant
            && input.CvConsistent
            && input.KellySizingOk
            && input.EquityCurveOk
            && input.TimeConcentrationOk
            && input.GenesisRegressionOk
            && input.TailRiskWithinThreshold;

        bool passed = (compositeGateOk || multiObjectiveGateOk) && safetyGatesOk;

        string failureReason = passed ? string.Empty :
            !input.HasSufficientOutOfSampleData ? "insufficient out-of-sample data for approval-grade validation"
            : input.DegradationFailed ? "excessive IS-to-OOS degradation"
            : !input.WfStable ? "walk-forward instability"
            : !input.MtfCompatible ? "higher TF regime incompatible"
            : !input.CorrelationSafe ? "winner params too similar to existing active strategy"
            : !input.SensitivityOk ? $"parameter sensitivity failure: {input.SensitivityReport}"
            : !input.CostSensitiveOk ? $"failed pessimistic cost test (score={input.PessimisticScore:F2})"
            : input.CILower < input.MinBootstrapCILower ? $"bootstrap CI lower bound too low ({input.CILower:F2} < {input.MinBootstrapCILower})"
            : !input.TemporalCorrelationSafe ? $"temporal signal overlap too high ({input.TemporalMaxOverlap:P0})"
            : !input.PortfolioCorrelationSafe ? $"portfolio PnL correlation too high ({input.PortfolioMaxCorrelation:P0})"
            : !input.PermSignificant ? $"permutation test not significant (p={input.PermPValue:F3}, Šidák α={input.PermCorrectedAlpha:F4})"
            : !input.CvConsistent ? $"cross-fold CV too high ({input.CvValue:F2} > {input.MaxCvCoefficientOfVariation})"
            : !input.KellySizingOk ? $"Kelly sizing fragility (Kelly Sharpe={input.KellySharpeRatio:F2} vs fixed={input.FixedLotSharpeRatio:F2})"
            : !input.EquityCurveOk ? "equity curve R² too low (non-linear equity growth)"
            : !input.TimeConcentrationOk ? "trade time concentration too high (clustering in narrow time windows)"
            : !input.TailRiskWithinThreshold ? $"tail risk VaR99 exceeds threshold (VaR99={input.TailRiskVaR99:F4})"
            : !input.GenesisRegressionOk ? "genesis quality regression — OOS Sharpe below 80% of original screening OOS Sharpe"
            : $"insufficient improvement ({input.CandidateImprovement:+0.00;-0.00}) or score ({input.OosHealthScore:F2}); " +
              $"multi-objective: Sharpe={input.SharpeRatio:F2}, DD={input.MaxDrawdownPct:F1}%, WR={input.WinRate:P0}, PF={input.ProfitFactor:F2}";

        var report = new Dictionary<string, object>
        {
            ["passed"] = passed,
            ["compositeGateOk"] = compositeGateOk,
            ["multiObjectiveGateOk"] = multiObjectiveGateOk,
            ["safetyGatesOk"] = safetyGatesOk,
            ["candidateImprovement"] = input.CandidateImprovement,
            ["oosHealthScore"] = input.OosHealthScore,
            ["hasSufficientOutOfSampleData"] = input.HasSufficientOutOfSampleData,
            ["ciLower"] = input.CILower,
            ["wfStable"] = input.WfStable,
            ["mtfCompatible"] = input.MtfCompatible,
            ["correlationSafe"] = input.CorrelationSafe,
            ["sensitivityOk"] = input.SensitivityOk,
            ["costSensitiveOk"] = input.CostSensitiveOk,
            ["temporalCorrelationSafe"] = input.TemporalCorrelationSafe,
            ["portfolioCorrelationSafe"] = input.PortfolioCorrelationSafe,
            ["permSignificant"] = input.PermSignificant,
            ["cvConsistent"] = input.CvConsistent,
            ["kellySizingOk"] = input.KellySizingOk,
            ["equityCurveOk"] = input.EquityCurveOk,
            ["timeConcentrationOk"] = input.TimeConcentrationOk,
            ["genesisRegressionOk"] = input.GenesisRegressionOk,
            ["tailRiskWithinThreshold"] = input.TailRiskWithinThreshold,
            ["tailRiskVaR99"] = input.TailRiskVaR99,
            ["failureReason"] = failureReason
        };

        return new Result(passed, compositeGateOk, multiObjectiveGateOk, safetyGatesOk, failureReason, report);
    }
}
