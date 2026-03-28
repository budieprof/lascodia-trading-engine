using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects ensemble diversity collapse in live model snapshots and queues a full
/// retrain with diversity regularisation explicitly elevated.
///
/// At training time, base learners in the bagged ensemble are forced apart via NCL
/// (<c>NclLambda</c>) and diversity regularisation (<c>DiversityLambda</c>). After
/// deployment, warm-start micro-retrains (<see cref="MLOnlineUpdateWorker"/>) run
/// with those terms disabled to keep updates fast. Over many micro-update cycles the
/// learners can slowly converge — their weight vectors become increasingly correlated
/// and the ensemble degenerates toward a single learner, eliminating the variance-
/// reduction benefit that made the ensemble robust.
///
/// This worker reads <see cref="ModelSnapshot.EnsembleDiversity"/> (average pairwise
/// Pearson correlation between base-learner weight vectors, stored at training time)
/// from each active model's <c>ModelBytes</c>. When diversity has collapsed above
/// <c>MaxEnsembleDiversity</c>, it queues a full retrain with elevated NCL and
/// diversity lambdas passed as <c>HyperparamOverrides</c> in the
/// <see cref="MLTrainingRun.HyperparamConfigJson"/>.
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLDiversityRecovery:PollIntervalSeconds</c> — default 21600 (6 h)</item>
///   <item><c>MLDiversityRecovery:MaxEnsembleDiversity</c>  — collapse trigger, default 0.75</item>
///   <item><c>MLDiversityRecovery:ForcedNclLambda</c>       — NCL lambda for recovery retrain, default 0.30</item>
///   <item><c>MLDiversityRecovery:ForcedDiversityLambda</c> — diversity lambda for recovery retrain, default 0.15</item>
/// </list>
/// </summary>
public sealed class MLEnsembleDiversityRecoveryWorker : BackgroundService
{
    private const string CK_PollSecs            = "MLDiversityRecovery:PollIntervalSeconds";
    private const string CK_MaxDiversity        = "MLDiversityRecovery:MaxEnsembleDiversity";
    private const string CK_ForcedNclLambda     = "MLDiversityRecovery:ForcedNclLambda";
    private const string CK_ForcedDivLambda     = "MLDiversityRecovery:ForcedDiversityLambda";

    private readonly IServiceScopeFactory                        _scopeFactory;
    private readonly ILogger<MLEnsembleDiversityRecoveryWorker>  _logger;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new async DI scope per poll cycle so scoped EF Core
    /// contexts are correctly disposed after each diversity check.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLEnsembleDiversityRecoveryWorker(
        IServiceScopeFactory                          scopeFactory,
        ILogger<MLEnsembleDiversityRecoveryWorker>    logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Polls every <c>MLDiversityRecovery:PollIntervalSeconds</c>
    /// seconds (default 21600 = 6 hours), reading the interval from <see cref="EngineConfig"/>
    /// on each cycle so it can be hot-reloaded without a restart.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLEnsembleDiversityRecoveryWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default 6-hour poll interval; overridden by EngineConfig on each iteration.
            int pollSecs = 21600;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Refresh poll interval from DB on every cycle to support hot-reload.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 21600, stoppingToken);

                await CheckDiversityAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLEnsembleDiversityRecoveryWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLEnsembleDiversityRecoveryWorker stopping.");
    }

    /// <summary>
    /// Iterates over all active models and delegates per-model diversity checks to
    /// <see cref="CheckModelDiversityAsync"/>. Reads the current diversity thresholds
    /// and forced regularisation lambdas from <see cref="EngineConfig"/> so they can
    /// be adjusted at runtime without a full restart.
    /// </summary>
    /// <param name="readCtx">Read-only EF context for querying models and config.</param>
    /// <param name="writeCtx">Write EF context for inserting retraining run records.</param>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task CheckDiversityAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load diversity control hyperparameters from EngineConfig.
        // These are intentionally separate from the model's training config so that
        // the diversity-recovery policy can be tuned without modifying training code.
        double maxDiversity      = await GetConfigAsync<double>(readCtx, CK_MaxDiversity,    0.75, ct);
        double forcedNclLambda   = await GetConfigAsync<double>(readCtx, CK_ForcedNclLambda, 0.30, ct);
        double forcedDivLambda   = await GetConfigAsync<double>(readCtx, CK_ForcedDivLambda, 0.15, ct);

        // Load active models that have a valid serialised snapshot.
        // AsNoTracking for read efficiency — we do not modify models in this context.
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckModelDiversityAsync(
                    model, readCtx, writeCtx,
                    maxDiversity, forcedNclLambda, forcedDivLambda, ct);
            }
            catch (Exception ex)
            {
                // Per-model exceptions are non-fatal — log and continue with other models.
                _logger.LogWarning(ex,
                    "Diversity recovery check failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    /// <summary>
    /// Checks whether the ensemble diversity of a single model has collapsed below the
    /// acceptable threshold and, if so, queues a full retraining run with elevated
    /// Negative Correlation Learning (NCL) and diversity regularisation hyperparameters.
    /// </summary>
    /// <remarks>
    /// Diversity recovery methodology:
    ///
    /// The <see cref="ModelSnapshot.EnsembleDiversity"/> field stores the average pairwise
    /// Pearson correlation between base-learner weight vectors, computed at training time
    /// and stored as a diagnostic. A value close to 0 means learners are diverse (good);
    /// a value close to 1 means learners have converged to nearly identical models (bad).
    ///
    /// When diversity exceeds <paramref name="maxDiversity"/>:
    /// <list type="number">
    ///   <item>
    ///     Skip if a training run (Queued or Running) is already present for this
    ///     symbol/timeframe — another trigger (PSI, freshness, etc.) has already
    ///     scheduled a retrain that will rebuild diversity from scratch.
    ///   </item>
    ///   <item>
    ///     Insert a new <see cref="MLTrainingRun"/> with elevated NCL lambda
    ///     (<paramref name="forcedNclLambda"/>) and diversity lambda
    ///     (<paramref name="forcedDivLambda"/>) embedded in <c>HyperparamConfigJson</c>.
    ///     <c>MLTrainingWorker</c> merges these overrides on top of the EngineConfig
    ///     baseline during training, forcing the new ensemble to diversify aggressively.
    ///   </item>
    /// </list>
    ///
    /// NCL (Negative Correlation Learning) adds a penalty term to the training loss that
    /// penalises learners for making correlated errors: L_k += −λ_ncl × Σ_{j≠k} e_k × e_j,
    /// where e_k is learner k's prediction error. This actively encourages learner
    /// specialisation without requiring separate training datasets.
    /// </remarks>
    /// <param name="model">The model being checked.</param>
    /// <param name="readCtx">Read-only EF context for querying training run status.</param>
    /// <param name="writeCtx">Write EF context for inserting the recovery retrain run.</param>
    /// <param name="maxDiversity">Collapse threshold above which recovery is triggered.</param>
    /// <param name="forcedNclLambda">NCL regularisation coefficient for the recovery run.</param>
    /// <param name="forcedDivLambda">Diversity regularisation coefficient for the recovery run.</param>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task CheckModelDiversityAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        double                                  maxDiversity,
        double                                  forcedNclLambda,
        double                                  forcedDivLambda,
        CancellationToken                       ct)
    {
        // Deserialise the model snapshot to read the stored EnsembleDiversity metric.
        ModelSnapshot? snap;
        try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
        catch { return; }

        if (snap is null || snap.Weights.Length == 0) return;

        // EnsembleDiversity = 0 when not yet computed (pre-Round-7 models); skip these.
        // A zero value is indistinguishable from a perfectly diverse ensemble and should
        // not trigger a false-positive recovery retrain.
        if (snap.EnsembleDiversity <= 0.0) return;

        _logger.LogDebug(
            "DiversityRecovery: {Symbol}/{Tf} model {Id}: diversity={Div:F3} threshold={Thr:F3}",
            model.Symbol, model.Timeframe, model.Id, snap.EnsembleDiversity, maxDiversity);

        // Diversity is within acceptable bounds — no action required.
        if (snap.EnsembleDiversity <= maxDiversity) return;

        _logger.LogWarning(
            "DiversityRecovery: {Symbol}/{Tf} model {Id}: ensemble diversity collapsed " +
            "({Div:F3} > {Thr:F3}). Queuing diversity-recovery retrain with " +
            "NclLambda={Ncl:F2} DiversityLambda={Div2:F2}.",
            model.Symbol, model.Timeframe, model.Id,
            snap.EnsembleDiversity, maxDiversity, forcedNclLambda, forcedDivLambda);

        // Skip if a full retrain is already queued or running.
        // Any scheduled retrain will produce a fresh model with the standard
        // diversity regularisation; a separate recovery run is redundant.
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);

        if (alreadyQueued)
        {
            _logger.LogDebug(
                "DiversityRecovery: {Symbol}/{Tf} model {Id} already has a retrain queued — skip.",
                model.Symbol, model.Timeframe, model.Id);
            return;
        }

        // Queue a full retrain with elevated diversity hyperparams passed as overrides.
        // HyperparamOverrides are merged on top of EngineConfig defaults in MLTrainingWorker,
        // so only the fields that differ from the base config need to be specified here.
        // The triggeredBy, ensembleDiversity, and modelId fields are included for audit.
        writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol    = model.Symbol,
            Timeframe = model.Timeframe,
            Status    = RunStatus.Queued,
            FromDate  = DateTime.UtcNow.AddDays(-365),
            ToDate    = DateTime.UtcNow,
            HyperparamConfigJson = JsonSerializer.Serialize(new
            {
                triggeredBy      = "MLEnsembleDiversityRecoveryWorker",
                ensembleDiversity = snap.EnsembleDiversity,
                maxDiversity,
                forcedNclLambda,
                forcedDivLambda,
                modelId          = model.Id,
                // Override keys recognised by MLTrainingWorker.LoadHyperparamsAsync:
                // NclLambda forces NCL penalty to push learners into diverse specialisations.
                // DiversityLambda adds a direct diversity term to the ensemble loss function.
                NclLambda        = forcedNclLambda,
                DiversityLambda  = forcedDivLambda,
            }),
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table,
    /// falling back to <paramref name="defaultValue"/> if the key is absent or
    /// the value cannot be converted to the target type.
    /// </summary>
    /// <typeparam name="T">Target value type (int, double, string, etc.).</typeparam>
    /// <param name="ctx">EF Core context to query against.</param>
    /// <param name="key">The EngineConfig key to look up.</param>
    /// <param name="defaultValue">Value to return when the key is missing or invalid.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed config value or <paramref name="defaultValue"/>.</returns>
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
