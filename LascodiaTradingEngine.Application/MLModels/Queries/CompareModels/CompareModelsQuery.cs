using MediatR;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.CompareModels;

// ── DTO ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Side-by-side comparison of a champion and challenger ML model, including
/// computed deltas and statistical significance of accuracy differences.
/// </summary>
public record ModelComparisonDto
{
    // ── Champion metrics ─────────────────────────────────────────────────
    public long     ChampionId                 { get; init; }
    public string   ChampionSymbol             { get; init; } = string.Empty;
    public string   ChampionTimeframe          { get; init; } = string.Empty;
    public string   ChampionArchitecture       { get; init; } = string.Empty;
    public double   ChampionDirectionAccuracy  { get; init; }
    public double   ChampionBrierScore         { get; init; }
    public double   ChampionSharpeRatio        { get; init; }
    public double   ChampionLiveAccuracy       { get; init; }
    public int      ChampionLivePredictions    { get; init; }
    public DateTime? ChampionActivatedAt       { get; init; }

    // ── Challenger metrics ───────────────────────────────────────────────
    public long     ChallengerId               { get; init; }
    public string   ChallengerSymbol           { get; init; } = string.Empty;
    public string   ChallengerTimeframe        { get; init; } = string.Empty;
    public string   ChallengerArchitecture     { get; init; } = string.Empty;
    public double   ChallengerDirectionAccuracy { get; init; }
    public double   ChallengerBrierScore       { get; init; }
    public double   ChallengerSharpeRatio      { get; init; }
    public double   ChallengerLiveAccuracy     { get; init; }
    public int      ChallengerLivePredictions  { get; init; }
    public DateTime? ChallengerActivatedAt     { get; init; }

    // ── Comparison deltas ────────────────────────────────────────────────
    public double   AccuracyDelta              { get; init; }
    public double   BrierDelta                 { get; init; }
    public double   SharpeDelta                { get; init; }

    /// <summary>
    /// Two-proportion z-test p-value for the difference in live direction accuracy.
    /// Lower values indicate statistically significant performance differences.
    /// </summary>
    public double   StatisticalSignificance    { get; init; }
}

// ── Query ────────────────────────────────────────────────────────────────────

/// <summary>
/// Compares two ML models (champion vs challenger) by loading their entities and
/// recent prediction logs, computing side-by-side performance metrics and a z-test
/// p-value for statistical significance of accuracy differences.
/// </summary>
public class CompareModelsQuery : IRequest<ResponseData<ModelComparisonDto>>
{
    /// <summary>Database ID of the champion (incumbent) model.</summary>
    public required long ChampionModelId { get; set; }

    /// <summary>Database ID of the challenger (candidate) model.</summary>
    public required long ChallengerModelId { get; set; }
}

// ── Handler ──────────────────────────────────────────────────────────────────

/// <summary>
/// Loads both models and their resolved prediction logs, computes live metrics,
/// and returns a <see cref="ModelComparisonDto"/> with deltas and significance.
/// </summary>
public class CompareModelsQueryHandler : IRequestHandler<CompareModelsQuery, ResponseData<ModelComparisonDto>>
{
    private readonly IReadApplicationDbContext _context;

    public CompareModelsQueryHandler(IReadApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<ModelComparisonDto>> Handle(
        CompareModelsQuery request, CancellationToken cancellationToken)
    {
        var ctx = _context.GetDbContext();

        // ── Load both models ─────────────────────────────────────────────
        var champion = await ctx.Set<MLModel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == request.ChampionModelId && !m.IsDeleted, cancellationToken);

        if (champion is null)
            return ResponseData<ModelComparisonDto>.Init(null, false, "Champion model not found", "-14");

        var challenger = await ctx.Set<MLModel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == request.ChallengerModelId && !m.IsDeleted, cancellationToken);

        if (challenger is null)
            return ResponseData<ModelComparisonDto>.Init(null, false, "Challenger model not found", "-14");

        // ── Load resolved prediction logs for each model ─────────────────
        var championLogs = await ctx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId == champion.Id &&
                        !l.IsDeleted &&
                        l.DirectionCorrect != null &&
                        l.OutcomeRecordedAt != null)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var challengerLogs = await ctx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId == challenger.Id &&
                        !l.IsDeleted &&
                        l.DirectionCorrect != null &&
                        l.OutcomeRecordedAt != null)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // ── Compute live metrics ─────────────────────────────────────────
        var champMetrics = ComputeLiveMetrics(championLogs);
        var challMetrics = ComputeLiveMetrics(challengerLogs);

        // ── Compute z-test significance ──────────────────────────────────
        double pValue = ComputeTwoProportionZTestPValue(
            champMetrics.Correct, champMetrics.Total,
            challMetrics.Correct, challMetrics.Total);

        var dto = new ModelComparisonDto
        {
            // Champion
            ChampionId                = champion.Id,
            ChampionSymbol            = champion.Symbol,
            ChampionTimeframe         = champion.Timeframe.ToString(),
            ChampionArchitecture      = champion.LearnerArchitecture.ToString(),
            ChampionDirectionAccuracy = champion.DirectionAccuracy.HasValue ? (double)champion.DirectionAccuracy.Value : 0,
            ChampionBrierScore        = champion.BrierScore.HasValue ? (double)champion.BrierScore.Value : 0,
            ChampionSharpeRatio       = champion.SharpeRatio.HasValue ? (double)champion.SharpeRatio.Value : 0,
            ChampionLiveAccuracy      = champMetrics.Accuracy,
            ChampionLivePredictions   = champMetrics.Total,
            ChampionActivatedAt       = champion.ActivatedAt,

            // Challenger
            ChallengerId                = challenger.Id,
            ChallengerSymbol            = challenger.Symbol,
            ChallengerTimeframe         = challenger.Timeframe.ToString(),
            ChallengerArchitecture      = challenger.LearnerArchitecture.ToString(),
            ChallengerDirectionAccuracy = challenger.DirectionAccuracy.HasValue ? (double)challenger.DirectionAccuracy.Value : 0,
            ChallengerBrierScore        = challenger.BrierScore.HasValue ? (double)challenger.BrierScore.Value : 0,
            ChallengerSharpeRatio       = challenger.SharpeRatio.HasValue ? (double)challenger.SharpeRatio.Value : 0,
            ChallengerLiveAccuracy      = challMetrics.Accuracy,
            ChallengerLivePredictions   = challMetrics.Total,
            ChallengerActivatedAt       = challenger.ActivatedAt,

            // Deltas (positive = challenger is better)
            AccuracyDelta = challMetrics.Accuracy - champMetrics.Accuracy,
            BrierDelta = (champion.BrierScore.HasValue && challenger.BrierScore.HasValue)
                ? (double)(champion.BrierScore.Value - challenger.BrierScore.Value) // positive = challenger better (lower Brier)
                : 0,
            SharpeDelta = (challenger.SharpeRatio.HasValue && champion.SharpeRatio.HasValue)
                ? (double)(challenger.SharpeRatio.Value - champion.SharpeRatio.Value)
                : 0,

            StatisticalSignificance = pValue,
        };

        return ResponseData<ModelComparisonDto>.Init(dto, true, "Successful", "00");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private record LiveMetrics(int Correct, int Total, double Accuracy);

    private static LiveMetrics ComputeLiveMetrics(List<MLModelPredictionLog> logs)
    {
        if (logs.Count == 0) return new LiveMetrics(0, 0, 0);
        int correct = logs.Count(l => l.DirectionCorrect == true);
        double accuracy = (double)correct / logs.Count;
        return new LiveMetrics(correct, logs.Count, accuracy);
    }

    /// <summary>
    /// Two-proportion z-test for the difference in accuracy between two models.
    /// Returns a two-sided p-value. Uses the pooled proportion under H0: p1 == p2.
    /// </summary>
    private static double ComputeTwoProportionZTestPValue(
        int correct1, int total1,
        int correct2, int total2)
    {
        if (total1 == 0 || total2 == 0) return 1.0;

        double p1 = (double)correct1 / total1;
        double p2 = (double)correct2 / total2;
        double pPooled = (double)(correct1 + correct2) / (total1 + total2);

        if (pPooled <= 0 || pPooled >= 1) return 1.0;

        double se = Math.Sqrt(pPooled * (1 - pPooled) * (1.0 / total1 + 1.0 / total2));
        if (se < 1e-15) return 1.0;

        double z = (p1 - p2) / se;

        // Two-sided p-value using the standard normal CDF approximation
        return 2.0 * NormalCdfComplement(Math.Abs(z));
    }

    /// <summary>
    /// Approximation of 1 - Phi(z) for the standard normal distribution using the
    /// Abramowitz and Stegun rational approximation (formula 26.2.17).
    /// Accurate to ~7.5e-8 for all z >= 0.
    /// </summary>
    private static double NormalCdfComplement(double z)
    {
        if (z < 0) return 1.0 - NormalCdfComplement(-z);

        const double p  = 0.2316419;
        const double b1 = 0.319381530;
        const double b2 = -0.356563782;
        const double b3 = 1.781477937;
        const double b4 = -1.821255978;
        const double b5 = 1.330274429;

        double t = 1.0 / (1.0 + p * z);
        double phi = Math.Exp(-0.5 * z * z) / Math.Sqrt(2 * Math.PI);
        double poly = ((((b5 * t + b4) * t + b3) * t + b2) * t + b1) * t;

        return phi * poly;
    }
}
