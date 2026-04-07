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
internal sealed record CandidateValidationResult(
    bool Passed,
    ScoredCandidate Winner,
    decimal OosHealthScore,
    BacktestResult OosResult,
    bool HasOosValidation,
    decimal CILower,
    decimal CIMedian,
    decimal CIUpper,
    double PermPValue,
    double PermCorrectedAlpha,
    bool PermSignificant,
    bool SensitivityOk,
    string SensitivityReport,
    bool CostSensitiveOk,
    decimal PessimisticScore,
    bool DegradationFailed,
    decimal WfAvgScore,
    bool WfStable,
    bool MtfCompatible,
    bool CorrelationSafe,
    bool TemporalCorrelationSafe,
    double TemporalMaxOverlap,
    bool PortfolioCorrelationSafe,
    double PortfolioMaxCorrelation,
    bool CvConsistent,
    double CvValue,
    string ApprovalReportJson,
    string FailureReason,
    IReadOnlyList<(int Rank, string Params, string Reason, decimal Score)>? FailedCandidates = null);

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

internal sealed class PipelinePersistenceState
{
    public bool CompletionPersisted { get; set; }
    public bool CompletionArtifactsPublished { get; set; }
}
