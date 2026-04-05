using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.MLModels.Queries.GenerateModelRiskReport;

// ── Query ────────────────────────────────────────────────────────────────────

/// <summary>
/// Generates a comprehensive risk report for a specific ML model, aggregating training metrics,
/// live performance, calibration data, robustness scores, drift status, shadow evaluation results,
/// and lifecycle event counts into a single DTO.
/// </summary>
public class GenerateModelRiskReportQuery : IRequest<ResponseData<ModelRiskReportDto>>
{
    /// <summary>Database ID of the ML model to generate the risk report for.</summary>
    public long MLModelId { get; set; }
}

// ── Handler ──────────────────────────────────────────────────────────────────

/// <summary>
/// Handles model risk report generation. Loads the ML model, its latest training run,
/// latest shadow evaluation, and lifecycle event count, then assembles them into a
/// ModelRiskReportDto for governance and monitoring purposes.
/// </summary>
public class GenerateModelRiskReportQueryHandler : IRequestHandler<GenerateModelRiskReportQuery, ResponseData<ModelRiskReportDto>>
{
    private readonly IReadApplicationDbContext _context;

    public GenerateModelRiskReportQueryHandler(IReadApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<ModelRiskReportDto>> Handle(GenerateModelRiskReportQuery request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();

        var model = await db.Set<MLModel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.MLModelId && !x.IsDeleted, cancellationToken);

        if (model is null)
            return ResponseData<ModelRiskReportDto>.Init(null, false, "ML model not found", "-14");

        // Latest training run
        var latestTrainingRun = await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .Where(x => x.MLModelId == request.MLModelId && !x.IsDeleted)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        // Latest shadow evaluation (where this model is either champion or challenger)
        var latestShadowEval = await db.Set<MLShadowEvaluation>()
            .AsNoTracking()
            .Where(x => (x.ChampionModelId == request.MLModelId || x.ChallengerModelId == request.MLModelId)
                        && !x.IsDeleted)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        // Lifecycle event count
        var lifecycleEventCount = await db.Set<MLModelLifecycleLog>()
            .AsNoTracking()
            .CountAsync(x => x.MLModelId == request.MLModelId && !x.IsDeleted, cancellationToken);

        var report = new ModelRiskReportDto(
            ModelId: model.Id,
            Symbol: model.Symbol,
            Timeframe: model.Timeframe.ToString(),
            ModelVersion: model.ModelVersion,
            LearnerArchitecture: model.LearnerArchitecture.ToString(),
            // Training metrics
            DirectionAccuracy: model.DirectionAccuracy ?? 0m,
            F1Score: model.F1Score,
            BrierScore: model.BrierScore,
            SharpeRatio: model.SharpeRatio,
            TrainingSamples: model.TrainingSamples,
            WalkForwardAvgAccuracy: model.WalkForwardAvgAccuracy,
            WalkForwardStdDev: model.WalkForwardStdDev,
            // Live performance
            LiveDirectionAccuracy: model.LiveDirectionAccuracy,
            LiveTotalPredictions: model.LiveTotalPredictions > 0 ? model.LiveTotalPredictions : null,
            LiveActiveDays: model.LiveActiveDays > 0 ? model.LiveActiveDays : null,
            // Calibration
            PlattA: model.PlattA,
            PlattB: model.PlattB,
            PlattCalibrationDrift: model.PlattCalibrationDrift.HasValue ? (decimal)model.PlattCalibrationDrift.Value : null,
            // Robustness
            FragilityScore: model.FragilityScore,
            DatasetHash: model.DatasetHash,
            // Drift status
            IsSuppressed: model.IsSuppressed,
            IsFallbackChampion: model.IsFallbackChampion,
            // Shadow evaluation
            LatestShadowStatus: latestShadowEval?.Status.ToString(),
            ShadowChallengerAccuracy: latestShadowEval?.ChallengerDirectionAccuracy,
            ShadowChampionAccuracy: latestShadowEval?.ChampionDirectionAccuracy,
            // Lifecycle
            LifecycleEventCount: lifecycleEventCount,
            // Timestamp
            GeneratedAt: DateTime.UtcNow);

        return ResponseData<ModelRiskReportDto>.Init(report, true, "Successful", "00");
    }
}
