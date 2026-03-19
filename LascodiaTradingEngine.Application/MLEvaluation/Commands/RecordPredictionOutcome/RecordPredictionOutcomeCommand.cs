using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLEvaluation.Commands.RecordPredictionOutcome;

// ── Command ───────────────────────────────────────────────────────────────────

public class RecordPredictionOutcomeCommand : IRequest<ResponseData<string>>
{
    public long    TradeSignalId        { get; set; }
    public required string ActualDirection { get; set; }
    public decimal ActualMagnitudePips  { get; set; }
    public bool    WasProfitable        { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class RecordPredictionOutcomeCommandValidator : AbstractValidator<RecordPredictionOutcomeCommand>
{
    public RecordPredictionOutcomeCommandValidator()
    {
        RuleFor(x => x.TradeSignalId)
            .GreaterThan(0).WithMessage("TradeSignalId must be greater than zero");

        RuleFor(x => x.ActualDirection)
            .NotEmpty().WithMessage("ActualDirection is required")
            .Must(d => Enum.TryParse<TradeDirection>(d, ignoreCase: true, out _)).WithMessage("ActualDirection must be 'Buy' or 'Sell'");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class RecordPredictionOutcomeCommandHandler : IRequestHandler<RecordPredictionOutcomeCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public RecordPredictionOutcomeCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(RecordPredictionOutcomeCommand request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();

        // 1. Find all prediction logs for the given trade signal
        var logs = await db.Set<Domain.Entities.MLModelPredictionLog>()
            .Where(x => x.TradeSignalId == request.TradeSignalId && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        if (!logs.Any())
            return ResponseData<string>.Init(null, false, "No prediction logs found for this trade signal", "-14");

        var now = DateTime.UtcNow;

        // 2. Update each log with outcome data
        var actualDirection = Enum.Parse<TradeDirection>(request.ActualDirection, ignoreCase: true);

        foreach (var log in logs)
        {
            log.ActualDirection      = actualDirection;
            log.ActualMagnitudePips  = request.ActualMagnitudePips;
            log.WasProfitable        = request.WasProfitable;
            log.DirectionCorrect     = log.PredictedDirection == actualDirection;
            log.OutcomeRecordedAt    = now;
        }

        // 3. Find running shadow evaluations for the Symbol+Timeframe of these logs
        var symbols    = logs.Select(x => x.Symbol).Distinct().ToList();
        var timeframes = logs.Select(x => x.Timeframe).Distinct().ToList();

        var evaluations = await db.Set<Domain.Entities.MLShadowEvaluation>()
            .Where(x => symbols.Contains(x.Symbol)
                     && timeframes.Contains(x.Timeframe)
                     && x.Status == ShadowEvaluationStatus.Running
                     && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var evaluation in evaluations)
        {
            evaluation.CompletedTrades++;

            // 4. If threshold reached, calculate metrics and decide promotion
            if (evaluation.CompletedTrades >= evaluation.RequiredTrades)
            {
                var windowLogs = await db.Set<Domain.Entities.MLModelPredictionLog>()
                    .Where(x => x.Symbol == evaluation.Symbol
                             && x.Timeframe == evaluation.Timeframe
                             && x.OutcomeRecordedAt != null
                             && !x.IsDeleted)
                    .ToListAsync(cancellationToken);

                var championLogs   = windowLogs.Where(x => x.MLModelId == evaluation.ChampionModelId   && x.ModelRole == ModelRole.Champion).ToList();
                var challengerLogs = windowLogs.Where(x => x.MLModelId == evaluation.ChallengerModelId && x.ModelRole == ModelRole.Challenger).ToList();

                // ── Direction accuracy ────────────────────────────────────────
                decimal championAccuracy   = championLogs.Any()
                    ? (decimal)championLogs.Count(x => x.DirectionCorrect == true) / championLogs.Count
                    : 0m;
                decimal challengerAccuracy = challengerLogs.Any()
                    ? (decimal)challengerLogs.Count(x => x.DirectionCorrect == true) / challengerLogs.Count
                    : 0m;

                // ── Brier score (lower is better) ─────────────────────────────
                // BrierScore = mean((confidence - actual)²), actual = 1 if correct else 0
                decimal championBrier   = ComputeBrierScore(championLogs);
                decimal challengerBrier = ComputeBrierScore(challengerLogs);

                // ── Magnitude correlation (Pearson, higher is better) ─────────
                decimal championMagCorr   = ComputeMagnitudeCorrelation(championLogs);
                decimal challengerMagCorr = ComputeMagnitudeCorrelation(challengerLogs);

                evaluation.ChampionDirectionAccuracy   = championAccuracy;
                evaluation.ChallengerDirectionAccuracy = challengerAccuracy;
                evaluation.ChampionBrierScore          = championBrier;
                evaluation.ChallengerBrierScore        = challengerBrier;
                evaluation.ChampionMagnitudeCorrelation   = championMagCorr;
                evaluation.ChallengerMagnitudeCorrelation = challengerMagCorr;
                evaluation.Status      = ShadowEvaluationStatus.Completed;
                evaluation.CompletedAt = now;

                // ── Multi-metric promotion: challenger must win majority of metrics
                // Metric 1: accuracy improvement exceeds PromotionThreshold (from config, not hardcoded)
                bool accuracyWin = challengerAccuracy > championAccuracy + evaluation.PromotionThreshold;
                // Metric 2: Brier score improved (lower is better)
                bool brierWin    = challengerLogs.Any() && championLogs.Any() && challengerBrier < championBrier;
                // Metric 3: Magnitude correlation improved (higher is better)
                bool magCorrWin  = challengerLogs.Any() && championLogs.Any() && challengerMagCorr > championMagCorr;

                int metricsWon  = (accuracyWin ? 1 : 0) + (brierWin ? 1 : 0) + (magCorrWin ? 1 : 0);
                bool anyAccGain = challengerAccuracy > championAccuracy;

                if (accuracyWin && metricsWon >= 2)
                {
                    evaluation.PromotionDecision = PromotionDecision.AutoPromoted;
                    evaluation.DecisionReason    =
                        $"Challenger wins {metricsWon}/3 metrics: " +
                        $"acc {challengerAccuracy:P1} vs {championAccuracy:P1} (+{evaluation.PromotionThreshold:P1} threshold), " +
                        $"brier {challengerBrier:F4} vs {championBrier:F4}, " +
                        $"magCorr {challengerMagCorr:F3} vs {championMagCorr:F3}";

                    var challenger = await db.Set<Domain.Entities.MLModel>()
                        .FirstOrDefaultAsync(x => x.Id == evaluation.ChallengerModelId && !x.IsDeleted, cancellationToken);
                    if (challenger != null)
                    {
                        challenger.IsActive    = true;
                        challenger.Status      = MLModelStatus.Active;
                        challenger.ActivatedAt = now;
                    }

                    var champion = await db.Set<Domain.Entities.MLModel>()
                        .FirstOrDefaultAsync(x => x.Id == evaluation.ChampionModelId && !x.IsDeleted, cancellationToken);
                    if (champion != null)
                    {
                        champion.IsActive = false;
                        champion.Status   = MLModelStatus.Superseded;
                    }
                }
                else if (anyAccGain)
                {
                    evaluation.PromotionDecision = PromotionDecision.FlaggedForReview;
                    evaluation.DecisionReason    =
                        $"Challenger has accuracy gain but did not meet auto-promotion criteria " +
                        $"(metrics won: {metricsWon}/3, threshold: {evaluation.PromotionThreshold:P1}). " +
                        $"acc {challengerAccuracy:P1} vs {championAccuracy:P1}, " +
                        $"brier {challengerBrier:F4} vs {championBrier:F4}";
                }
                else
                {
                    evaluation.PromotionDecision = PromotionDecision.Rejected;
                    evaluation.DecisionReason    =
                        $"Challenger accuracy {challengerAccuracy:P1} does not exceed champion {championAccuracy:P1}. " +
                        $"brier {challengerBrier:F4} vs {championBrier:F4}";
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Outcome recorded successfully", true, "Successful", "00");
    }

    // ── Metric helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Brier score = mean((ConfidenceScore − actual)²) where actual = 1 if DirectionCorrect.
    /// Lower values indicate better-calibrated probability forecasts.
    /// Returns 0.25 (random baseline) when no resolved logs exist.
    /// </summary>
    private static decimal ComputeBrierScore(
        IReadOnlyList<Domain.Entities.MLModelPredictionLog> logs)
    {
        var resolved = logs.Where(x => x.DirectionCorrect.HasValue).ToList();
        if (resolved.Count == 0) return 0.25m; // random-classifier baseline

        decimal sum = resolved.Sum(x =>
        {
            decimal actual = x.DirectionCorrect == true ? 1m : 0m;
            decimal diff   = x.ConfidenceScore - actual;
            return diff * diff;
        });
        return sum / resolved.Count;
    }

    /// <summary>
    /// Pearson correlation between predicted and actual magnitude in pips.
    /// Returns 0 when fewer than two observations have magnitude data.
    /// </summary>
    private static decimal ComputeMagnitudeCorrelation(
        IReadOnlyList<Domain.Entities.MLModelPredictionLog> logs)
    {
        var pairs = logs
            .Where(x => x.ActualMagnitudePips.HasValue)
            .Select(x => (pred: (double)x.PredictedMagnitudePips,
                          actual: (double)x.ActualMagnitudePips!.Value))
            .ToList();

        if (pairs.Count < 2) return 0m;

        double meanPred   = pairs.Average(p => p.pred);
        double meanActual = pairs.Average(p => p.actual);

        double num  = pairs.Sum(p => (p.pred - meanPred) * (p.actual - meanActual));
        double denP = Math.Sqrt(pairs.Sum(p => Math.Pow(p.pred   - meanPred,   2)));
        double denA = Math.Sqrt(pairs.Sum(p => Math.Pow(p.actual - meanActual, 2)));

        double denom = denP * denA;
        return denom < 1e-10 ? 0m : (decimal)(num / denom);
    }
}
