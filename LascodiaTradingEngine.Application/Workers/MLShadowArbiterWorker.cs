using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Resolves running <see cref="MLShadowEvaluation"/> records by comparing the live prediction
/// accuracy of the challenger model against the champion and making a final promotion decision.
///
/// Promotion rules (evaluated once <see cref="MLShadowEvaluation.RequiredTrades"/> resolved
/// predictions exist for the challenger, or the evaluation has expired):
/// <list type="bullet">
///   <item><b>AutoPromoted</b> — challenger accuracy exceeds champion by ≥ promotion threshold
///         across ALL active market-regime buckets.</item>
///   <item><b>PartialSufficiency AutoPromoted</b> — ≥ 50% of <see cref="MLShadowEvaluation.RequiredTrades"/>
///         collected AND challenger leads by ≥ 2× the threshold. Avoids permanently blocking
///         promotion on low-volume symbols.</item>
///   <item><b>Rejected</b> — champion outperforms challenger by ≥ threshold; challenger retired.</item>
///   <item><b>FlaggedForReview</b> — inconclusive margin, regime disagreement, or expired with
///         insufficient data. Champion retained; human review required.</item>
/// </list>
/// </summary>
public sealed class MLShadowArbiterWorker : BackgroundService
{
    private const string CK_PollSecs       = "MLShadow:PollIntervalSeconds";
    private const string CK_ShadowMinZScore = "MLTraining:ShadowMinZScore";

    private readonly IServiceScopeFactory           _scopeFactory;
    private readonly ILogger<MLShadowArbiterWorker> _logger;

    public MLShadowArbiterWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<MLShadowArbiterWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLShadowArbiterWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 300;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var ctx      = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 300, stoppingToken);

                await ProcessRunningShadowEvalsAsync(ctx, writeCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLShadowArbiterWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLShadowArbiterWorker stopping.");
    }

    // ── Main evaluation loop ──────────────────────────────────────────────────

    private async Task ProcessRunningShadowEvalsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        double shadowMinZScore = await GetConfigAsync<double>(readCtx, CK_ShadowMinZScore, 1.645, ct);

        var running = await readCtx.Set<MLShadowEvaluation>()
            .Where(s => s.Status == ShadowEvaluationStatus.Running && !s.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        _logger.LogDebug("Shadow arbiter found {Count} running evaluation(s).", running.Count);

        foreach (var shadow in running)
        {
            ct.ThrowIfCancellationRequested();
            await EvaluateShadowAsync(shadow, readCtx, writeCtx, shadowMinZScore, ct);
        }
    }

    private async Task EvaluateShadowAsync(
        MLShadowEvaluation                      shadow,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        double                                  shadowMinZScore,
        CancellationToken                       ct)
    {
        bool isExpired = shadow.ExpiresAt.HasValue && shadow.ExpiresAt.Value < DateTime.UtcNow;

        // ── Fetch resolved prediction logs for both models ────────────────────
        var challengerLogs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == shadow.ChallengerModelId &&
                        l.DirectionCorrect != null                     &&
                        !l.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        var championLogs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == shadow.ChampionModelId &&
                        l.DirectionCorrect != null                   &&
                        !l.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        int  completedTrades = challengerLogs.Count;
        bool hasEnoughData   = completedTrades >= shadow.RequiredTrades;

        // ── Partial sufficiency check ─────────────────────────────────────────
        // Auto-promote early when ≥ 50% of required trades are collected and
        // the challenger leads by ≥ 2× the promotion threshold. Prevents
        // challengers from being permanently blocked on low-volume symbols.
        double threshold        = (double)shadow.PromotionThreshold;
        bool   partialSufficient = completedTrades >= shadow.RequiredTrades / 2 &&
                                   completedTrades > 0;

        if (!hasEnoughData && !isExpired && !partialSufficient)
        {
            _logger.LogDebug(
                "Shadow eval {Id} ({Symbol}/{Tf}): {N}/{Required} trades — waiting for more outcomes.",
                shadow.Id, shadow.Symbol, shadow.Timeframe,
                completedTrades, shadow.RequiredTrades);
            return;
        }

        // ── Compute overall metrics for both models ───────────────────────────
        double challengerAcc     = DirectionAccuracy(challengerLogs);
        double championAcc       = DirectionAccuracy(championLogs);
        double challengerBrier   = BrierScore(challengerLogs);
        double championBrier     = BrierScore(championLogs);
        double challengerMagCorr = MagnitudeCorrelation(challengerLogs);
        double championMagCorr   = MagnitudeCorrelation(championLogs);

        // ── Regime-conditioned accuracy ───────────────────────────────────────
        // Load recent regime snapshots and tag each prediction log with its regime.
        var regimeSnapshots = await readCtx.Set<MarketRegimeSnapshot>()
            .Where(r => r.Symbol    == shadow.Symbol    &&
                        r.Timeframe == shadow.Timeframe &&
                        !r.IsDeleted)
            .OrderByDescending(r => r.DetectedAt)
            .Take(500)
            .AsNoTracking()
            .ToListAsync(ct);

        var regimeSegments = ComputeRegimeSegmentedAccuracy(
            challengerLogs, championLogs, regimeSnapshots);

        // ── Make promotion decision ───────────────────────────────────────────
        PromotionDecision decision;
        string            reason;

        if (isExpired && !hasEnoughData && !partialSufficient)
        {
            decision = PromotionDecision.FlaggedForReview;
            reason   = $"Evaluation expired with only {completedTrades}/{shadow.RequiredTrades} " +
                       $"resolved trades. No statistically valid decision possible. " +
                       $"Champion retained by default.";
        }
        else
        {
            double overallMargin = challengerAcc - championAcc;
            double effectiveThreshold = partialSufficient && !hasEnoughData
                ? threshold * 2.0 // require a stronger lead for early promotion
                : threshold;

            // ── SPRT early stopping (Wald sequential test) ────────────────────
            // H0: challenger accuracy = championAcc (p0)
            // H1: challenger accuracy = championAcc + threshold (p1)
            // α = 0.05, β = 0.20 → boundaries A = ln(16) ≈ 2.773, B = ln(0.2105) ≈ −1.558
            double sprtLlr = ComputeSprtLlr(challengerLogs, p0: championAcc, p1: championAcc + threshold);
            const double SprtUpperBound =  2.773;  // ln((1 − β) / α) = ln(0.80 / 0.05)
            const double SprtLowerBound = -1.558;  // ln(β / (1 − α)) = ln(0.20 / 0.95)

            bool sprtPromote = sprtLlr >= SprtUpperBound;
            bool sprtReject  = sprtLlr <= SprtLowerBound;

            // Check regime-level results (challenger must not lose badly in any regime)
            bool regimeFailed = regimeSegments.Any(kv =>
                kv.Value.Count >= 10 &&
                kv.Value.ChallengerAcc < kv.Value.ChampionAcc - threshold);

            if (regimeFailed)
            {
                var failedRegimes = regimeSegments
                    .Where(kv => kv.Value.Count >= 10 && kv.Value.ChallengerAcc < kv.Value.ChampionAcc - threshold)
                    .Select(kv => $"{kv.Key}(chal={kv.Value.ChallengerAcc:P1} champ={kv.Value.ChampionAcc:P1} n={kv.Value.Count})")
                    .ToList();

                decision = PromotionDecision.FlaggedForReview;
                reason   = $"Challenger underperforms champion in regime(s): {string.Join(", ", failedRegimes)}. " +
                           $"Overall: chal={challengerAcc:P1} champ={championAcc:P1}. Flagged for human review.";
            }
            else if (sprtPromote)
            {
                // SPRT upper boundary crossed — statistically significant superiority
                decision = PromotionDecision.AutoPromoted;
                reason   = $"SPRT early promotion: LLR={sprtLlr:F3} ≥ {SprtUpperBound} (α=0.05, β=0.20). " +
                           $"Challenger acc={challengerAcc:P1} vs champion acc={championAcc:P1} " +
                           $"(n={completedTrades}). Brier: chal={challengerBrier:F4} champ={championBrier:F4}.";
            }
            else if (sprtReject)
            {
                // SPRT lower boundary crossed — challenger is reliably worse
                decision = PromotionDecision.Rejected;
                reason   = $"SPRT early rejection: LLR={sprtLlr:F3} ≤ {SprtLowerBound} (α=0.05, β=0.20). " +
                           $"Champion acc={championAcc:P1} reliably outperforms challenger acc={challengerAcc:P1} " +
                           $"(n={completedTrades}). Challenger retired.";
            }
            else if (overallMargin >= effectiveThreshold)
            {
                double pValue  = TwoProportionPValue(challengerLogs, championLogs);
                double zScore  = TwoProportionZScore(challengerLogs, championLogs);
                string earlyTag = partialSufficient && !hasEnoughData ? " (partial sufficiency — 2× threshold)" : string.Empty;

                if (pValue < 0.05 && zScore >= shadowMinZScore)
                {
                    decision = PromotionDecision.AutoPromoted;
                    reason   = $"Challenger acc={challengerAcc:P1} beats champion acc={championAcc:P1} " +
                               $"by {overallMargin:P2} ≥ threshold {effectiveThreshold:P2}{earlyTag}. " +
                               $"Two-proportion z-test p={pValue:F4} < 0.05, z={zScore:F3} ≥ {shadowMinZScore:F3} (statistically significant). " +
                               $"SPRT LLR={sprtLlr:F3}. " +
                               $"Brier: challenger={challengerBrier:F4} champion={championBrier:F4}.";
                }
                else
                {
                    decision = PromotionDecision.FlaggedForReview;
                    reason   = $"Challenger acc={challengerAcc:P1} exceeds champion by {overallMargin:P2} ≥ threshold{earlyTag} " +
                               $"but two-proportion z-test p={pValue:F4} z={zScore:F3} " +
                               $"(requires p<0.05 and z≥{shadowMinZScore:F3} — not met). " +
                               $"SPRT LLR={sprtLlr:F3} (inconclusive). " +
                               $"Flagged for human review — collect more data.";
                }
            }
            else if (overallMargin <= -threshold)
            {
                decision = PromotionDecision.Rejected;
                reason   = $"Champion acc={championAcc:P1} outperforms challenger acc={challengerAcc:P1} " +
                           $"by {-overallMargin:P2} ≥ threshold {threshold:P2}. Challenger retired.";
            }
            else
            {
                decision = PromotionDecision.FlaggedForReview;
                reason   = $"Overall margin {overallMargin:P2} within ±{threshold:P2} threshold. " +
                           $"SPRT LLR={sprtLlr:F3} (inconclusive). " +
                           $"Results inconclusive — champion retained, flagged for human review.";
            }
        }

        _logger.LogInformation(
            "Shadow eval {Id} ({Symbol}/{Tf}): decision={Decision} " +
            "challenger={ChalAcc:P1} champion={ChampAcc:P1} trades={N}",
            shadow.Id, shadow.Symbol, shadow.Timeframe,
            decision, challengerAcc, championAcc, completedTrades);

        var now = DateTime.UtcNow;

        // ── Persist the resolved evaluation ──────────────────────────────────
        await writeCtx.Set<MLShadowEvaluation>()
            .Where(s => s.Id == shadow.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status,                        ShadowEvaluationStatus.Completed)
                .SetProperty(x => x.CompletedAt,                   now)
                .SetProperty(x => x.CompletedTrades,               completedTrades)
                .SetProperty(x => x.PromotionDecision,             decision)
                .SetProperty(x => x.DecisionReason,                reason)
                .SetProperty(x => x.ChallengerDirectionAccuracy,   (decimal)challengerAcc)
                .SetProperty(x => x.ChallengerBrierScore,          (decimal)challengerBrier)
                .SetProperty(x => x.ChallengerMagnitudeCorrelation,(decimal)challengerMagCorr)
                .SetProperty(x => x.ChampionDirectionAccuracy,     (decimal)championAcc)
                .SetProperty(x => x.ChampionBrierScore,            (decimal)championBrier)
                .SetProperty(x => x.ChampionMagnitudeCorrelation,  (decimal)championMagCorr),
                ct);

        // ── Persist per-regime accuracy breakdown ────────────────────────────
        foreach (var kv in regimeSegments)
        {
            // Only persist regimes with meaningful data
            if (kv.Value.Count == 0) continue;

            writeCtx.Set<MLShadowRegimeBreakdown>().Add(new MLShadowRegimeBreakdown
            {
                ShadowEvaluationId = shadow.Id,
                Regime             = kv.Key,
                TotalPredictions   = kv.Value.Count,
                ChampionAccuracy   = (decimal)kv.Value.ChampionAcc,
                ChallengerAccuracy = (decimal)kv.Value.ChallengerAcc,
                AccuracyDelta      = (decimal)(kv.Value.ChallengerAcc - kv.Value.ChampionAcc),
            });
        }

        if (regimeSegments.Any(kv => kv.Value.Count > 0))
            await writeCtx.SaveChangesAsync(ct);

        // ── Act on the decision ───────────────────────────────────────────────
        if (decision == PromotionDecision.AutoPromoted)
        {
            await PromoteChallengerAsync(shadow, writeCtx, ct);
        }
        else
        {
            await writeCtx.Set<MLModel>()
                .Where(m => m.Id == shadow.ChallengerModelId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.IsActive, false)
                    .SetProperty(m => m.Status,   MLModelStatus.Superseded),
                    ct);

            _logger.LogInformation(
                "Shadow eval {Id}: challenger model {ChalId} retired (decision={Decision}).",
                shadow.Id, shadow.ChallengerModelId, decision);
        }
    }

    private async Task PromoteChallengerAsync(
        MLShadowEvaluation                      shadow,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        var now = DateTime.UtcNow;

        // ── Snapshot champion's live performance before superseding ───────────
        // Mirror the same retirement snapshot written by MLTrainingWorker so that both
        // promotion paths (direct training promotion and shadow-arbiter promotion) produce
        // consistent live stats on the outgoing champion.
        try
        {
            var liveLogs = await writeCtx.Set<MLModelPredictionLog>()
                .Where(l => l.MLModelId == shadow.ChampionModelId &&
                            l.DirectionCorrect != null            &&
                            !l.IsDeleted)
                .AsNoTracking()
                .Select(l => new { l.DirectionCorrect })
                .ToListAsync(ct);

            if (liveLogs.Count > 0)
            {
                decimal liveAcc  = (decimal)liveLogs.Count(l => l.DirectionCorrect == true) / liveLogs.Count;
                int     liveN    = liveLogs.Count;

                var champion = await writeCtx.Set<MLModel>()
                    .FirstOrDefaultAsync(m => m.Id == shadow.ChampionModelId, ct);

                if (champion is not null)
                {
                    int activeDays = champion.ActivatedAt.HasValue
                        ? (int)(now - champion.ActivatedAt.Value).TotalDays
                        : 0;

                    await writeCtx.Set<MLModel>()
                        .Where(m => m.Id == shadow.ChampionModelId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.LiveDirectionAccuracy, liveAcc)
                            .SetProperty(m => m.LiveTotalPredictions,  liveN)
                            .SetProperty(m => m.LiveActiveDays,        activeDays),
                            ct);

                    _logger.LogInformation(
                        "Shadow arbiter: champion {Id} retirement snapshot live_acc={Acc:P1} n={N} days={D}",
                        shadow.ChampionModelId, liveAcc, liveN, activeDays);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Shadow arbiter: failed to snapshot live performance for champion {Id} — non-critical",
                shadow.ChampionModelId);
        }

        await writeCtx.Set<MLModel>()
            .Where(m => m.Id == shadow.ChampionModelId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsActive, false)
                .SetProperty(m => m.Status,   MLModelStatus.Superseded),
                ct);

        await writeCtx.Set<MLModel>()
            .Where(m => m.Id == shadow.ChallengerModelId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsActive,    true)
                .SetProperty(m => m.Status,      MLModelStatus.Active)
                .SetProperty(m => m.ActivatedAt, now),
                ct);

        _logger.LogWarning(
            "Shadow eval {Id} ({Symbol}/{Tf}): challenger {ChalId} PROMOTED to champion. " +
            "Previous champion {ChampId} superseded.",
            shadow.Id, shadow.Symbol, shadow.Timeframe,
            shadow.ChallengerModelId, shadow.ChampionModelId);
    }

    // ── Regime-segmented accuracy ─────────────────────────────────────────────

    private record RegimeBucket(double ChallengerAcc, double ChampionAcc, int Count);

    /// <summary>
    /// Tags each prediction log with the market regime that was active at its prediction time,
    /// then computes per-regime accuracy for challenger and champion.
    /// </summary>
    private static Dictionary<MarketRegimeEnum, RegimeBucket> ComputeRegimeSegmentedAccuracy(
        List<MLModelPredictionLog> challengerLogs,
        List<MLModelPredictionLog> championLogs,
        List<MarketRegimeSnapshot> regimeSnapshots)
    {
        if (regimeSnapshots.Count == 0)
            return new Dictionary<MarketRegimeEnum, RegimeBucket>();

        // Build a sorted list for efficient binary search
        var snapList = regimeSnapshots.OrderBy(r => r.DetectedAt).ToList();

        MarketRegimeEnum RegimeAt(DateTime ts)
        {
            // Last snapshot at or before ts
            var snap = snapList.LastOrDefault(r => r.DetectedAt <= ts);
            return snap?.Regime ?? MarketRegimeEnum.Ranging;
        }

        // Group challenger logs by regime
        var challengerByRegime = challengerLogs
            .GroupBy(l => RegimeAt(l.PredictedAt))
            .ToDictionary(g => g.Key, g => g.ToList());

        var championByRegime = championLogs
            .GroupBy(l => RegimeAt(l.PredictedAt))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<MarketRegimeEnum, RegimeBucket>();

        foreach (var regime in challengerByRegime.Keys)
        {
            var chalBucket  = challengerByRegime[regime];
            var champBucket = championByRegime.GetValueOrDefault(regime, []);

            result[regime] = new RegimeBucket(
                ChallengerAcc: DirectionAccuracy(chalBucket),
                ChampionAcc:   DirectionAccuracy(champBucket),
                Count:         chalBucket.Count);
        }

        return result;
    }

    // ── Metric helpers ────────────────────────────────────────────────────────

    private static double DirectionAccuracy(List<MLModelPredictionLog> logs)
    {
        if (logs.Count == 0) return 0;
        return logs.Count(l => l.DirectionCorrect == true) / (double)logs.Count;
    }

    private static double BrierScore(List<MLModelPredictionLog> logs)
    {
        if (logs.Count == 0) return 0;
        double sum = 0;
        int    n   = 0;
        foreach (var l in logs)
        {
            if (l.DirectionCorrect is null) continue;
            double conf = (double)l.ConfidenceScore;
            double pBuy = l.PredictedDirection == TradeDirection.Buy
                ? 0.5 + conf / 2.0
                : 0.5 - conf / 2.0;
            double y   = l.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
            sum += (pBuy - y) * (pBuy - y);
            n++;
        }
        return n > 0 ? sum / n : 0;
    }

    private static double MagnitudeCorrelation(List<MLModelPredictionLog> logs)
    {
        var valid = logs.Where(l => l.ActualMagnitudePips.HasValue).ToList();
        if (valid.Count < 2) return 0;

        var pred   = valid.Select(l => (double)l.PredictedMagnitudePips).ToArray();
        var actual = valid.Select(l => (double)l.ActualMagnitudePips!.Value).ToArray();

        double meanP = pred.Average(), meanA = actual.Average();
        double num = 0, denP = 0, denA = 0;

        for (int i = 0; i < pred.Length; i++)
        {
            double dp = pred[i] - meanP, da = actual[i] - meanA;
            num  += dp * da;
            denP += dp * dp;
            denA += da * da;
        }

        return (denP > 0 && denA > 0) ? num / Math.Sqrt(denP * denA) : 0;
    }

    // ── Statistical significance ──────────────────────────────────────────────

    /// <summary>
    /// One-tailed two-proportion z-test p-value (H1: challenger accuracy > champion accuracy).
    /// Returns 1.0 when sample sizes are too small for a reliable test.
    /// </summary>
    private static double TwoProportionPValue(
        List<MLModelPredictionLog> challengerLogs,
        List<MLModelPredictionLog> championLogs)
    {
        int n1 = challengerLogs.Count;
        int n2 = championLogs.Count;
        if (n1 < 10 || n2 < 10) return 1.0;

        int x1 = challengerLogs.Count(l => l.DirectionCorrect == true);
        int x2 = championLogs.Count(l => l.DirectionCorrect == true);

        double p1    = (double)x1 / n1;
        double p2    = (double)x2 / n2;
        double pPool = (double)(x1 + x2) / (n1 + n2);
        double se    = Math.Sqrt(pPool * (1 - pPool) * (1.0 / n1 + 1.0 / n2));

        if (se < 1e-10) return 1.0;

        double z = (p1 - p2) / se;
        return 1.0 - NormalCdf(z);   // one-tailed P(Z > z)
    }

    /// <summary>
    /// Returns the raw z-score from the one-tailed two-proportion z-test.
    /// Returns 0.0 when sample sizes are too small.
    /// </summary>
    private static double TwoProportionZScore(
        List<MLModelPredictionLog> challengerLogs,
        List<MLModelPredictionLog> championLogs)
    {
        int n1 = challengerLogs.Count;
        int n2 = championLogs.Count;
        if (n1 < 10 || n2 < 10) return 0.0;

        int    x1    = challengerLogs.Count(l => l.DirectionCorrect == true);
        int    x2    = championLogs.Count(l => l.DirectionCorrect == true);
        double p1    = (double)x1 / n1;
        double p2    = (double)x2 / n2;
        double pPool = (double)(x1 + x2) / (n1 + n2);
        double se    = Math.Sqrt(pPool * (1 - pPool) * (1.0 / n1 + 1.0 / n2));

        return se < 1e-10 ? 0.0 : (p1 - p2) / se;
    }

    // ── SPRT helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the cumulative log-likelihood ratio for the Wald SPRT.
    /// LLR = Σ log(P(Xi | p1) / P(Xi | p0)) over all resolved challenger predictions.
    /// Clamped to [−10, 10] to prevent floating-point extremes on degenerate probabilities.
    /// </summary>
    private static double ComputeSprtLlr(
        List<MLModelPredictionLog> logs,
        double                     p0,
        double                     p1)
    {
        // Guard against degenerate probability values
        p0 = Math.Clamp(p0, 0.01, 0.99);
        p1 = Math.Clamp(p1, 0.01, 0.99);

        if (p0 >= p1 || logs.Count == 0) return 0.0;

        double logLr1 = Math.Log(p1 / p0);          // contribution when prediction is correct
        double logLr0 = Math.Log((1 - p1) / (1 - p0)); // contribution when prediction is incorrect

        double llr = 0.0;
        foreach (var log in logs)
        {
            if (log.DirectionCorrect is null) continue;
            llr += log.DirectionCorrect.Value ? logLr1 : logLr0;
        }

        return Math.Clamp(llr, -10.0, 10.0);
    }

    private static double NormalCdf(double x)
        => 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0)));

    /// <summary>
    /// Abramowitz &amp; Stegun §7.1.26 polynomial approximation of erf(x).
    /// Maximum absolute error ≈ 1.5 × 10⁻⁷.
    /// </summary>
    private static double Erf(double x)
    {
        const double a1 =  0.254829592;
        const double a2 = -0.284496736;
        const double a3 =  1.421413741;
        const double a4 = -1.453152027;
        const double a5 =  1.061405429;
        const double p  =  0.3275911;

        int sign = x < 0 ? -1 : 1;
        x = Math.Abs(x);
        double t    = 1.0 / (1.0 + p * x);
        double poly = t * (a1 + t * (a2 + t * (a3 + t * (a4 + t * a5))));
        return sign * (1.0 - poly * Math.Exp(-x * x));
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
