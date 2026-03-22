using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Activates a fallback champion when the primary model is suppressed, and deactivates
/// the fallback when the primary recovers or a new model is promoted.
///
/// When <see cref="MLSignalSuppressionWorker"/> sets <see cref="MLModel.IsSuppressed"/>
/// on the active model, signal scoring is blocked. This worker bridges the gap by
/// re-activating the most recently superseded predecessor as a fallback champion
/// (<see cref="MLModel.IsFallbackChampion"/> = true). <see cref="Services.MLSignalScorer"/>
/// checks for fallback champions when the primary is suppressed.
///
/// Lifecycle:
/// <list type="bullet">
///   <item><b>Activate fallback:</b> when a model is suppressed and no fallback exists,
///         find the most recently superseded model for the same symbol/timeframe and set
///         <c>IsFallbackChampion = true</c>.</item>
///   <item><b>Deactivate fallback:</b> when the primary is un-suppressed (accuracy recovered)
///         or a new model is promoted (primary replaced), clear <c>IsFallbackChampion</c>
///         on all fallback models for that symbol/timeframe.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLSuppressionRollback:PollIntervalSeconds</c> — default 120 (2 min)</item>
/// </list>
/// </summary>
public sealed class MLSuppressionRollbackWorker : BackgroundService
{
    private const string CK_PollSecs = "MLSuppressionRollback:PollIntervalSeconds";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLSuppressionRollbackWorker> _logger;

    public MLSuppressionRollbackWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLSuppressionRollbackWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Main background loop. Runs at <c>MLSuppressionRollback:PollIntervalSeconds</c>
    /// intervals (default 120 s / 2 min) until the host requests shutdown.
    ///
    /// Each cycle:
    /// <list type="number">
    ///   <item>Calls <see cref="ActivateFallbacksAsync"/> to bridge newly suppressed models
    ///         with a predecessor fallback so signal scoring is not completely blocked.</item>
    ///   <item>Calls <see cref="DeactivateStaleFallbacksAsync"/> to retire fallbacks whose
    ///         primary model has recovered or been replaced, preventing stale fallbacks from
    ///         persisting indefinitely after the suppression condition clears.</item>
    /// </list>
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSuppressionRollbackWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 120;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readCtx = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(readCtx, CK_PollSecs, 120, stoppingToken);

                // Run activate before deactivate to prevent a race where a newly suppressed model
                // gets its fallback immediately deactivated in the same cycle.
                await ActivateFallbacksAsync(readCtx, writeCtx, stoppingToken);
                await DeactivateStaleFallbacksAsync(readCtx, writeCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLSuppressionRollbackWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLSuppressionRollbackWorker stopping.");
    }

    /// <summary>
    /// For each suppressed active model that has no fallback champion, activate the most
    /// recently superseded predecessor as a fallback.
    /// </summary>
    private async Task ActivateFallbacksAsync(
        DbContext readCtx,
        DbContext writeCtx,
        CancellationToken ct)
    {
        var suppressedModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && m.IsSuppressed && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in suppressedModels)
        {
            ct.ThrowIfCancellationRequested();

            // Check if a fallback already exists for this symbol/timeframe
            bool fallbackExists = await readCtx.Set<MLModel>()
                .AnyAsync(m => m.Symbol == model.Symbol &&
                               m.Timeframe == model.Timeframe &&
                               m.IsFallbackChampion &&
                               !m.IsDeleted, ct);

            if (fallbackExists)
                continue;

            // Find the most recently superseded model (previous champion).
            // Selection criteria:
            //   - Same symbol/timeframe as the suppressed model.
            //   - Status = Superseded (was previously the champion).
            //   - Has non-null ModelBytes so MLSignalScorer can deserialise and score with it.
            //   - Not the suppressed model itself (guards against self-reference edge cases).
            //   - Ordered by ActivatedAt descending so the most recently active champion is preferred
            //     (it will have the best recent feature alignment with current market conditions).
            var previous = await readCtx.Set<MLModel>()
                .Where(m => m.Symbol == model.Symbol &&
                            m.Timeframe == model.Timeframe &&
                            m.Status == MLModelStatus.Superseded &&
                            m.Id != model.Id &&
                            m.ModelBytes != null &&
                            !m.IsDeleted)
                .OrderByDescending(m => m.ActivatedAt)
                .FirstOrDefaultAsync(ct);

            if (previous is null)
            {
                _logger.LogWarning(
                    "SuppressionRollback: no superseded model available as fallback " +
                    "for {Symbol}/{Tf} (suppressed model {Id}).",
                    model.Symbol, model.Timeframe, model.Id);
                continue;
            }

            // Activate the fallback champion.
            // Setting IsActive = true allows MLSignalScorer to discover this model via its
            // standard active-model query. The IsFallbackChampion flag differentiates it
            // from a true primary champion, enabling the scorer to apply any fallback-specific
            // confidence discounting if configured.
            await writeCtx.Set<MLModel>()
                .Where(m => m.Id == previous.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.IsFallbackChampion, true)
                    .SetProperty(m => m.IsActive, true), ct);

            _logger.LogWarning(
                "SuppressionRollback: ACTIVATED fallback champion model {FallbackId} " +
                "for {Symbol}/{Tf} (primary model {PrimaryId} is suppressed). " +
                "Previous accuracy: {Acc:P1}.",
                previous.Id, model.Symbol, model.Timeframe, model.Id,
                previous.LiveDirectionAccuracy);
        }
    }

    /// <summary>
    /// Deactivate fallback champions when they are no longer needed:
    /// - The primary model is no longer suppressed (accuracy recovered)
    /// - The primary model was replaced by a new promoted model
    /// </summary>
    private async Task DeactivateStaleFallbacksAsync(
        DbContext readCtx,
        DbContext writeCtx,
        CancellationToken ct)
    {
        var fallbacks = await readCtx.Set<MLModel>()
            .Where(m => m.IsFallbackChampion && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var fallback in fallbacks)
        {
            ct.ThrowIfCancellationRequested();

            // Check if there is still a suppressed primary for this symbol/timeframe
            bool primaryStillSuppressed = await readCtx.Set<MLModel>()
                .AnyAsync(m => m.Symbol == fallback.Symbol &&
                               m.Timeframe == fallback.Timeframe &&
                               m.IsActive &&
                               m.IsSuppressed &&
                               m.Id != fallback.Id &&
                               !m.IsDeleted, ct);

            if (primaryStillSuppressed)
                continue;

            // Primary is no longer suppressed or has been replaced — deactivate fallback
            await writeCtx.Set<MLModel>()
                .Where(m => m.Id == fallback.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.IsFallbackChampion, false)
                    .SetProperty(m => m.IsActive, false), ct);

            _logger.LogInformation(
                "SuppressionRollback: DEACTIVATED fallback champion model {FallbackId} " +
                "for {Symbol}/{Tf} — primary model is no longer suppressed.",
                fallback.Id, fallback.Symbol, fallback.Timeframe);
        }
    }

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/> or returns <paramref name="defaultValue"/>
    /// when the key is absent or its stored string cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        DbContext ctx,
        string key,
        T defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
