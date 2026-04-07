using System.Text.Json;
using System.Text.Json.Serialization;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationApprovalReportParser
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal sealed class ApprovalReport
    {
        public bool? Passed { get; set; }
        public bool? CompositeGateOk { get; set; }
        public bool? MultiObjectiveGateOk { get; set; }
        public bool? SafetyGatesOk { get; set; }
        public decimal? CandidateImprovement { get; set; }
        public decimal? OosHealthScore { get; set; }
        public bool? HasSufficientOutOfSampleData { get; set; }
        public decimal? CiLower { get; set; }
        public bool? WfStable { get; set; }
        public bool? MtfCompatible { get; set; }
        public bool? CorrelationSafe { get; set; }
        public bool? SensitivityOk { get; set; }
        public bool? CostSensitiveOk { get; set; }
        public bool? TemporalCorrelationSafe { get; set; }
        public bool? PortfolioCorrelationSafe { get; set; }
        public bool? PermSignificant { get; set; }
        public bool? CvConsistent { get; set; }
        public bool? KellySizingOk { get; set; }
        public bool? EquityCurveOk { get; set; }
        public bool? TimeConcentrationOk { get; set; }
        public bool? GenesisRegressionOk { get; set; }
        public bool? TailRiskWithinThreshold { get; set; }
        public double? TailRiskVaR99 { get; set; }
        public string? FailureReason { get; set; }
        public string? TopCandidateFailureReason { get; set; }
        public string? ApprovalBlockedReason { get; set; }
        public decimal? FailedCandidateScore { get; set; }
        public string? FailedCandidateScoreSource { get; set; }
        public bool? HasOosValidation { get; set; }
        public decimal? InSampleHealthScore { get; set; }
        public IReadOnlyList<FailedCandidateDiagnostic>? FailedCandidates { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    internal sealed record FailedCandidateDiagnostic(
        int Rank,
        string Params,
        string Reason,
        decimal Score);

    internal static bool TryParse(string? approvalReportJson, out ApprovalReport report)
    {
        if (string.IsNullOrWhiteSpace(approvalReportJson))
        {
            report = new ApprovalReport();
            return true;
        }

        try
        {
            report = JsonSerializer.Deserialize<ApprovalReport>(approvalReportJson, s_jsonOptions)
                ?? new ApprovalReport();
            return true;
        }
        catch (JsonException)
        {
            report = new ApprovalReport();
            return false;
        }
    }

    internal static ApprovalReport Parse(string? approvalReportJson)
    {
        _ = TryParse(approvalReportJson, out var report);
        return report;
    }

    internal static string Serialize(ApprovalReport report)
        => JsonSerializer.Serialize(report, s_jsonOptions);

    internal static bool HasApprovalGradeOosValidation(string? approvalReportJson)
        => Parse(approvalReportJson).HasSufficientOutOfSampleData == true;

    internal static string MarkStrategyRemoved(string? approvalReportJson)
    {
        const string reason = "Strategy removed before approved parameters could be applied.";
        var report = Parse(approvalReportJson);
        report.Passed = false;
        report.TopCandidateFailureReason = reason;
        report.ApprovalBlockedReason = "StrategyRemoved";
        report.FailureReason ??= reason;
        return Serialize(report);
    }

    internal static string SetManualReviewDiagnostics(
        string? approvalReportJson,
        IReadOnlyList<FailedCandidateDiagnostic>? failedCandidates,
        string failureReason,
        decimal failedCandidateScore,
        bool hasOosValidation)
    {
        var report = Parse(approvalReportJson);
        report.Passed = false;
        report.FailureReason = failureReason;
        report.TopCandidateFailureReason = failureReason;
        report.ApprovalBlockedReason = "ManualReview";
        report.FailedCandidateScore = failedCandidateScore;
        report.FailedCandidateScoreSource = hasOosValidation ? "OutOfSample" : "InSample";
        report.HasOosValidation = hasOosValidation;
        report.HasSufficientOutOfSampleData = hasOosValidation;
        report.FailedCandidates = failedCandidates;
        return Serialize(report);
    }
}
