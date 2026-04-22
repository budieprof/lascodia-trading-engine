using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Applies production lifecycle state transitions that result from signal-level A/B tests.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IMLModelLifecycleTransitionService))]
public sealed class MLModelLifecycleTransitionService : IMLModelLifecycleTransitionService
{
    private readonly ILogger<MLModelLifecycleTransitionService> _logger;

    public MLModelLifecycleTransitionService(ILogger<MLModelLifecycleTransitionService> logger)
    {
        _logger = logger;
    }

    public async Task PromoteChallengerAsync(
        DbContext writeDb,
        long championId,
        long challengerId,
        string symbol,
        Timeframe timeframe,
        CancellationToken cancellationToken)
    {
        var existingPromotionLog = await PromotionAlreadyLoggedAsync(
            writeDb, championId, challengerId, cancellationToken);

        if (existingPromotionLog)
        {
            _logger.LogInformation(
                "A/B promotion for challenger {Challenger} over champion {Champion} already logged; skipping duplicate model transition.",
                challengerId, championId);
            return;
        }

        var now = DateTime.UtcNow;
        var champion = await writeDb.Set<MLModel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == championId, cancellationToken);
        var challenger = await writeDb.Set<MLModel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == challengerId, cancellationToken);

        var liveLogs = await writeDb.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId == championId &&
                        l.DirectionCorrect != null &&
                        !l.IsDeleted)
            .Select(l => l.DirectionCorrect)
            .ToListAsync(cancellationToken);

        decimal? liveAccuracy = liveLogs.Count > 0
            ? (decimal)liveLogs.Count(x => x == true) / liveLogs.Count
            : null;
        int activeDays = champion?.ActivatedAt.HasValue == true
            ? Math.Max(0, (int)(now - champion.ActivatedAt.Value).TotalDays)
            : 0;

        var activeModels = await writeDb.Set<MLModel>()
            .Where(m => m.Symbol == symbol &&
                        m.Timeframe == timeframe &&
                        m.IsActive &&
                        !m.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var activeModel in activeModels)
        {
            activeModel.IsActive = false;
            activeModel.IsFallbackChampion = false;
            activeModel.Status = MLModelStatus.Superseded;
        }

        var championToSnapshot = activeModels.FirstOrDefault(m => m.Id == championId)
            ?? await writeDb.Set<MLModel>().FirstOrDefaultAsync(m => m.Id == championId, cancellationToken);
        if (championToSnapshot is not null)
        {
            championToSnapshot.LiveDirectionAccuracy = liveAccuracy;
            championToSnapshot.LiveTotalPredictions = liveLogs.Count;
            championToSnapshot.LiveActiveDays = activeDays;
        }

        var challengerToPromote = await writeDb.Set<MLModel>()
            .FirstOrDefaultAsync(m => m.Id == challengerId, cancellationToken);
        if (challengerToPromote is null)
            return;

        challengerToPromote.IsActive = true;
        challengerToPromote.IsSuppressed = false;
        challengerToPromote.IsFallbackChampion = false;
        challengerToPromote.Status = MLModelStatus.Active;
        challengerToPromote.ActivatedAt = now;
        challengerToPromote.PreviousChampionModelId = championId;

        writeDb.Set<MLModelLifecycleLog>().Add(new MLModelLifecycleLog
        {
            MLModelId                = challengerId,
            EventType                = MLModelLifecycleEventType.AbTestPromotion,
            PreviousStatus           = challenger?.Status,
            NewStatus                = MLModelStatus.Active,
            PreviousChampionModelId  = championId,
            Reason                   = $"Promoted via signal-level A/B test. Previous champion: {championId}.",
            DirectionAccuracyAtTransition = challenger?.DirectionAccuracy,
            LiveAccuracyAtTransition = challenger?.LiveDirectionAccuracy,
            BrierScoreAtTransition   = challenger?.BrierScore,
            OccurredAt               = now,
        });

        writeDb.Set<MLModelLifecycleLog>().Add(new MLModelLifecycleLog
        {
            MLModelId    = championId,
            EventType    = MLModelLifecycleEventType.AbTestDemotion,
            PreviousStatus = champion?.Status,
            NewStatus    = MLModelStatus.Superseded,
            Reason       = $"Demoted by signal-level A/B test. New champion: {challengerId}.",
            DirectionAccuracyAtTransition = champion?.DirectionAccuracy,
            LiveAccuracyAtTransition = liveAccuracy ?? champion?.LiveDirectionAccuracy,
            BrierScoreAtTransition = champion?.BrierScore,
            OccurredAt   = now,
        });

        if (!await SaveLifecycleChangesOrDetectDuplicateAsync(
                writeDb,
                "abtest_promotion",
                () => PromotionAlreadyLoggedAsync(writeDb, championId, challengerId, cancellationToken),
                cancellationToken))
        {
            _logger.LogInformation(
                "A/B promotion for challenger {Challenger} over champion {Champion} was already committed concurrently.",
                challengerId, championId);
            return;
        }

        _logger.LogInformation(
            "A/B test promotion: model {Challenger} promoted to champion for {Symbol}/{Timeframe}. " +
            "Previous champion {Champion} demoted to Superseded.",
            challengerId, symbol, timeframe, championId);
    }

    public async Task RejectChallengerAsync(
        DbContext writeDb,
        long challengerId,
        CancellationToken cancellationToken)
    {
        var challenger = await writeDb.Set<MLModel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == challengerId, cancellationToken);
        var now = DateTime.UtcNow;

        var existingRejectionLog = await RejectionAlreadyLoggedAsync(
            writeDb, challengerId, cancellationToken);

        if (existingRejectionLog)
            return;

        var challengerToReject = await writeDb.Set<MLModel>()
            .FirstOrDefaultAsync(m => m.Id == challengerId, cancellationToken);
        if (challengerToReject is null)
            return;

        challengerToReject.Status = MLModelStatus.Superseded;
        challengerToReject.IsActive = false;
        challengerToReject.IsFallbackChampion = false;

        writeDb.Set<MLModelLifecycleLog>().Add(new MLModelLifecycleLog
        {
            MLModelId    = challengerId,
            EventType    = MLModelLifecycleEventType.AbTestRejection,
            PreviousStatus = challenger?.Status,
            NewStatus    = MLModelStatus.Superseded,
            Reason       = "Rejected by signal-level A/B test. Champion retained.",
            DirectionAccuracyAtTransition = challenger?.DirectionAccuracy,
            LiveAccuracyAtTransition = challenger?.LiveDirectionAccuracy,
            BrierScoreAtTransition = challenger?.BrierScore,
            OccurredAt   = now,
        });

        if (!await SaveLifecycleChangesOrDetectDuplicateAsync(
                writeDb,
                "abtest_rejection",
                () => RejectionAlreadyLoggedAsync(writeDb, challengerId, cancellationToken),
                cancellationToken))
        {
            _logger.LogInformation(
                "A/B rejection for challenger {Challenger} was already committed concurrently.",
                challengerId);
            return;
        }

        _logger.LogInformation(
            "A/B test rejection: challenger model {Challenger} superseded. Champion retained.",
            challengerId);
    }

    private static async Task<bool> SaveLifecycleChangesOrDetectDuplicateAsync(
        DbContext writeDb,
        string savepointNamePrefix,
        Func<Task<bool>> duplicateExistsAsync,
        CancellationToken cancellationToken)
    {
        var currentTransaction = writeDb.Database.CurrentTransaction;
        var savepointName = $"{savepointNamePrefix}_{Guid.NewGuid():N}";

        if (currentTransaction is not null)
            await currentTransaction.CreateSavepointAsync(savepointName, cancellationToken);

        try
        {
            await writeDb.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            if (currentTransaction is not null)
                await currentTransaction.RollbackToSavepointAsync(savepointName, cancellationToken);

            writeDb.ChangeTracker.Clear();
            if (!await duplicateExistsAsync())
                throw;

            return false;
        }
    }

    private static Task<bool> PromotionAlreadyLoggedAsync(
        DbContext writeDb,
        long championId,
        long challengerId,
        CancellationToken cancellationToken)
        => writeDb.Set<MLModelLifecycleLog>()
            .AsNoTracking()
            .AnyAsync(l => l.MLModelId == challengerId &&
                           l.EventType == MLModelLifecycleEventType.AbTestPromotion &&
                           l.PreviousChampionModelId == championId &&
                           !l.IsDeleted, cancellationToken);

    private static Task<bool> RejectionAlreadyLoggedAsync(
        DbContext writeDb,
        long challengerId,
        CancellationToken cancellationToken)
        => writeDb.Set<MLModelLifecycleLog>()
            .AsNoTracking()
            .AnyAsync(l => l.MLModelId == challengerId &&
                           l.EventType == MLModelLifecycleEventType.AbTestRejection &&
                           !l.IsDeleted, cancellationToken);
}
