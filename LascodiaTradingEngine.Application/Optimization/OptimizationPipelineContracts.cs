using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Shared contracts used across the optimization pipeline. These types live in the
/// optimization namespace so extracted services do not depend on worker internals.
/// </summary>
internal sealed record ScoredCandidate(
    string ParamsJson,
    decimal HealthScore,
    BacktestResult Result,
    double CvCoefficientOfVariation = 0.0)
{
    public bool TradesTrimmed { get; set; }
}

/// <summary>
/// A scored parameter candidate from the screening phases.
/// Check <see cref="ScoredCandidate.TradesTrimmed"/> before accessing
/// <c>Result.Trades</c> because low-scoring candidates may have their trade lists
/// cleared to reduce heap pressure.
/// </summary>
internal sealed record CandidateValidationResult
{
    public bool Passed { get; init; }
    public ScoredCandidate Winner { get; init; } = null!;
    public decimal OosHealthScore { get; init; }
    public BacktestResult OosResult { get; init; } = null!;
    public bool HasOosValidation { get; init; }
    public decimal CILower { get; init; }
    public decimal CIMedian { get; init; }
    public decimal CIUpper { get; init; }
    public double PermPValue { get; init; }
    public double PermCorrectedAlpha { get; init; }
    public bool PermSignificant { get; init; }
    public bool SensitivityOk { get; init; }
    public string SensitivityReport { get; init; } = string.Empty;
    public bool CostSensitiveOk { get; init; }
    public decimal PessimisticScore { get; init; }
    public bool DegradationFailed { get; init; }
    public decimal WfAvgScore { get; init; }
    public bool WfStable { get; init; }
    public bool MtfCompatible { get; init; }
    public bool CorrelationSafe { get; init; }
    public bool TemporalCorrelationSafe { get; init; }
    public double TemporalMaxOverlap { get; init; }
    public bool PortfolioCorrelationSafe { get; init; }
    public double PortfolioMaxCorrelation { get; init; }
    public bool CvConsistent { get; init; }
    public double CvValue { get; init; }
    public string ApprovalReportJson { get; init; } = string.Empty;
    public string FailureReason { get; init; } = string.Empty;
    public IReadOnlyList<(int Rank, string Params, string Reason, decimal Score)>? FailedCandidates { get; init; }

    internal static CandidateValidationResult Create(
        bool passed,
        ScoredCandidate winner,
        decimal oosHealthScore,
        BacktestResult oosResult,
        bool hasOosValidation,
        decimal ciLower,
        decimal ciMedian,
        decimal ciUpper,
        double permPValue,
        double permCorrectedAlpha,
        bool permSignificant,
        bool sensitivityOk,
        string sensitivityReport,
        bool costSensitiveOk,
        decimal pessimisticScore,
        bool degradationFailed,
        decimal wfAvgScore,
        bool wfStable,
        bool mtfCompatible,
        bool correlationSafe,
        bool temporalCorrelationSafe,
        double temporalMaxOverlap,
        bool portfolioCorrelationSafe,
        double portfolioMaxCorrelation,
        bool cvConsistent,
        double cvValue,
        string approvalReportJson,
        string failureReason,
        IReadOnlyList<(int Rank, string Params, string Reason, decimal Score)>? failedCandidates = null)
    {
        if (!OptimizationApprovalReportParser.TryParse(approvalReportJson, out var approvalReport))
        {
            throw new ArgumentException(
                "Candidate validation result contains malformed approval-report JSON.",
                nameof(approvalReportJson));
        }

        bool reportHasOosValidation = approvalReport.HasSufficientOutOfSampleData == true;
        if (reportHasOosValidation != hasOosValidation)
        {
            throw new ArgumentException(
                $"Candidate validation result is inconsistent: report OOS flag={reportHasOosValidation} but HasOosValidation={hasOosValidation}.",
                nameof(hasOosValidation));
        }

        if (approvalReport.HasOosValidation.HasValue && approvalReport.HasOosValidation.Value != hasOosValidation)
        {
            throw new ArgumentException(
                $"Candidate validation result is inconsistent: report HasOosValidation={approvalReport.HasOosValidation.Value} but HasOosValidation={hasOosValidation}.",
                nameof(hasOosValidation));
        }

        if (approvalReport.Passed.HasValue && approvalReport.Passed.Value != passed)
        {
            throw new ArgumentException(
                $"Candidate validation result is inconsistent: report Passed={approvalReport.Passed.Value} but Passed={passed}.",
                nameof(passed));
        }

        if (passed && !hasOosValidation)
        {
            throw new ArgumentException(
                "A passing candidate validation result must include approval-grade out-of-sample validation.",
                nameof(hasOosValidation));
        }

        if (passed && !string.IsNullOrWhiteSpace(approvalReport.FailureReason))
        {
            throw new ArgumentException(
                "A passing candidate validation result cannot carry a failure reason in the approval report.",
                nameof(approvalReportJson));
        }

        if (!passed
            && !string.IsNullOrWhiteSpace(failureReason)
            && !string.IsNullOrWhiteSpace(approvalReport.FailureReason)
            && !string.Equals(approvalReport.FailureReason, failureReason, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Candidate validation result is inconsistent: report failure reason '{approvalReport.FailureReason}' does not match '{failureReason}'.",
                nameof(failureReason));
        }

        return new CandidateValidationResult
        {
            Passed = passed,
            Winner = winner,
            OosHealthScore = oosHealthScore,
            OosResult = oosResult,
            HasOosValidation = hasOosValidation,
            CILower = ciLower,
            CIMedian = ciMedian,
            CIUpper = ciUpper,
            PermPValue = permPValue,
            PermCorrectedAlpha = permCorrectedAlpha,
            PermSignificant = permSignificant,
            SensitivityOk = sensitivityOk,
            SensitivityReport = sensitivityReport,
            CostSensitiveOk = costSensitiveOk,
            PessimisticScore = pessimisticScore,
            DegradationFailed = degradationFailed,
            WfAvgScore = wfAvgScore,
            WfStable = wfStable,
            MtfCompatible = mtfCompatible,
            CorrelationSafe = correlationSafe,
            TemporalCorrelationSafe = temporalCorrelationSafe,
            TemporalMaxOverlap = temporalMaxOverlap,
            PortfolioCorrelationSafe = portfolioCorrelationSafe,
            PortfolioMaxCorrelation = portfolioMaxCorrelation,
            CvConsistent = cvConsistent,
            CvValue = cvValue,
            ApprovalReportJson = approvalReportJson,
            FailureReason = failureReason,
            FailedCandidates = failedCandidates,
        };
    }
}

/// <summary>Results from the data loading + baseline evaluation phase.</summary>
internal sealed record DataLoadResult(
    Strategy Strategy,
    List<Candle> AllCandles,
    List<Candle> TrainCandles,
    List<Candle> TestCandles,
    int EmbargoSize,
    BacktestOptions ScreeningOptions,
    OptimizationGridBuilder.DataProtocol Protocol,
    DateTime CandleLookbackStart,
    MarketRegimeEnum? CurrentRegimeForBaseline,
    decimal BaselineComparisonScore,
    string BaselineParametersJson,
    CurrencyPair? PairInfo = null,
    bool BaselineRegimeParamsUsed = false);

/// <summary>Results from the surrogate-guided search phase.</summary>
internal sealed record SearchResult(
    List<ScoredCandidate> EvaluatedCandidates,
    int TotalIterations,
    string SurrogateKind,
    int WarmStartedObservations,
    bool ResumedFromCheckpoint,
    SearchExecutionSummary Diagnostics);

internal sealed record SearchExecutionSummary(
    string? AbortReason,
    bool CircuitBreakerTripped,
    int SuccessfulEvaluations,
    int FailedEvaluations,
    int TimedOutEvaluations,
    int ExceptionFailures,
    int DuplicateSuggestionsSkipped,
    int PeakConsecutiveFailures,
    int RecentFailureSampleCount,
    double RecentFailureRate);

/// <summary>Bundles scoped dependencies and tokens shared across optimization stages.</summary>
internal sealed record RunContext(
    OptimizationRun Run,
    Strategy Strategy,
    OptimizationConfig Config,
    decimal BaselineComparisonScore,
    DbContext Db,
    DbContext WriteDb,
    IWriteApplicationDbContext WriteCtx,
    IMediator Mediator,
    IAlertDispatcher AlertDispatcher,
    IIntegrationEventService EventService,
    CancellationToken Ct,
    CancellationToken RunCt);
