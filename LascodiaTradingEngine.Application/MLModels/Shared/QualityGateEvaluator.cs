using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Shared;

/// <summary>
/// Pure decision logic for ML model quality gates. Extracted from MLTrainingWorker
/// for direct unit testing without mocking infrastructure.
/// </summary>
public static class QualityGateEvaluator
{
    public record QualityGateInput(
        // Model metrics
        double Accuracy,
        double ExpectedValue,
        double BrierScore,
        double SharpeRatio,
        double F1,
        double OobAccuracy,
        // Walk-forward CV
        double WfStdAccuracy,
        // Calibration
        double Ece,
        double BrierSkillScore,
        // Thresholds (from hyperparams)
        double MinAccuracy,
        double MinExpectedValue,
        double MaxBrierScore,
        double MinSharpeRatio,
        double MinF1Score,
        double MaxWfStdDev,
        double MaxEce,
        double MinBrierSkillScore,
        double MinQualityRetentionRatio,
        double ParentOobAccuracy,
        // Regime context
        bool IsTrending,
        double TrendingMinAccuracy,
        double TrendingMinEV,
        // Bypass thresholds
        double EvBypassMinEV,
        double EvBypassMinSharpe,
        double BrierBypassMinEV,
        double BrierBypassMinSharpe,
        bool PortfolioCorrelationOk = true);

    public record QualityGateResult(
        bool Passed,
        bool F1Passed,
        bool EvBypassF1,
        bool BrierBypassed,
        double EffectiveBrierCeiling,
        bool QualityRegressionFailed,
        string? FailureReason);

    /// <summary>
    /// Evaluates all quality gates against the provided metrics. Pure function — no I/O.
    /// </summary>
    public static QualityGateResult Evaluate(QualityGateInput input)
    {
        // Quality regression check
        bool qualityRegressionFailed =
            input.MinQualityRetentionRatio > 0.0 &&
            input.ParentOobAccuracy > 0.0 &&
            input.OobAccuracy < input.ParentOobAccuracy * input.MinQualityRetentionRatio;

        // EV-based F1 bypass
        bool evBypassF1 = input.ExpectedValue >= input.EvBypassMinEV
                       && input.SharpeRatio >= input.EvBypassMinSharpe;

        // F1 gate (regime-conditional)
        bool f1Passed = input.IsTrending
            ? (input.F1 >= input.MinF1Score || (input.Accuracy >= input.TrendingMinAccuracy && input.ExpectedValue >= input.TrendingMinEV))
            : (input.MinF1Score <= 0 || input.F1 >= input.MinF1Score || evBypassF1);

        // Brier bypass
        double brierCeiling = input.MaxBrierScore;
        bool brierBypassed = false;
        if (input.ExpectedValue >= input.BrierBypassMinEV && input.SharpeRatio >= input.BrierBypassMinSharpe
            && input.BrierScore > input.MaxBrierScore && input.BrierScore <= input.MaxBrierScore * 1.05)
        {
            brierCeiling = input.MaxBrierScore * 1.05;
            brierBypassed = true;
        }

        bool portfolioOk = input.PortfolioCorrelationOk;

        bool passed =
            input.Accuracy >= input.MinAccuracy &&
            input.ExpectedValue >= input.MinExpectedValue &&
            input.BrierScore <= brierCeiling &&
            (input.MinSharpeRatio <= 0 || input.SharpeRatio >= input.MinSharpeRatio) &&
            f1Passed &&
            input.WfStdAccuracy <= input.MaxWfStdDev &&
            (input.MaxEce <= 0 || input.Ece <= input.MaxEce) &&
            (input.MinBrierSkillScore <= -1.0 || input.BrierSkillScore >= input.MinBrierSkillScore) &&
            !qualityRegressionFailed &&
            portfolioOk;

        // Build failure reason
        string? failureReason = passed ? null : BuildFailureReason(input, brierCeiling, f1Passed, evBypassF1, qualityRegressionFailed);

        return new QualityGateResult(passed, f1Passed, evBypassF1, brierBypassed, brierCeiling, qualityRegressionFailed, failureReason);
    }

    private static string BuildFailureReason(QualityGateInput i, double brierCeiling, bool f1Passed, bool evBypass, bool oobFailed)
    {
        var reasons = new List<string>();
        if (i.Accuracy < i.MinAccuracy) reasons.Add($"accuracy={i.Accuracy:P1}<{i.MinAccuracy:P1}");
        if (i.ExpectedValue < i.MinExpectedValue) reasons.Add($"ev={i.ExpectedValue:F4}<{i.MinExpectedValue:F4}");
        if (i.BrierScore > brierCeiling) reasons.Add($"brier={i.BrierScore:F4}>{brierCeiling:F4}");
        if (i.SharpeRatio < i.MinSharpeRatio) reasons.Add($"sharpe={i.SharpeRatio:F2}<{i.MinSharpeRatio:F2}");
        if (!f1Passed) reasons.Add($"f1={i.F1:F3}<{i.MinF1Score:F3}");
        if (i.WfStdAccuracy > i.MaxWfStdDev) reasons.Add($"wfStd={i.WfStdAccuracy:P1}>{i.MaxWfStdDev:P1}");
        if (i.MaxEce > 0 && i.Ece > i.MaxEce) reasons.Add($"ece={i.Ece:F4}>{i.MaxEce:F4}");
        if (i.MinBrierSkillScore > -1.0 && i.BrierSkillScore < i.MinBrierSkillScore) reasons.Add($"bss={i.BrierSkillScore:F4}<{i.MinBrierSkillScore:F4}");
        if (oobFailed) reasons.Add($"oobRegression");
        if (!i.PortfolioCorrelationOk) reasons.Add("portfolioCorrelationFailed");
        return string.Join(", ", reasons);
    }
}
