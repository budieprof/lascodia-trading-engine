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

    public MLEnsembleDiversityRecoveryWorker(
        IServiceScopeFactory                          scopeFactory,
        ILogger<MLEnsembleDiversityRecoveryWorker>    logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLEnsembleDiversityRecoveryWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 21600;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

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

    private async Task CheckDiversityAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        double maxDiversity      = await GetConfigAsync<double>(readCtx, CK_MaxDiversity,    0.75, ct);
        double forcedNclLambda   = await GetConfigAsync<double>(readCtx, CK_ForcedNclLambda, 0.30, ct);
        double forcedDivLambda   = await GetConfigAsync<double>(readCtx, CK_ForcedDivLambda, 0.15, ct);

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
                _logger.LogWarning(ex,
                    "Diversity recovery check failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    private async Task CheckModelDiversityAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        double                                  maxDiversity,
        double                                  forcedNclLambda,
        double                                  forcedDivLambda,
        CancellationToken                       ct)
    {
        ModelSnapshot? snap;
        try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
        catch { return; }

        if (snap is null || snap.Weights.Length == 0) return;

        // EnsembleDiversity = 0 when not yet computed (pre-Round-7 models); skip
        if (snap.EnsembleDiversity <= 0.0) return;

        _logger.LogDebug(
            "DiversityRecovery: {Symbol}/{Tf} model {Id}: diversity={Div:F3} threshold={Thr:F3}",
            model.Symbol, model.Timeframe, model.Id, snap.EnsembleDiversity, maxDiversity);

        if (snap.EnsembleDiversity <= maxDiversity) return;

        _logger.LogWarning(
            "DiversityRecovery: {Symbol}/{Tf} model {Id}: ensemble diversity collapsed " +
            "({Div:F3} > {Thr:F3}). Queuing diversity-recovery retrain with " +
            "NclLambda={Ncl:F2} DiversityLambda={Div2:F2}.",
            model.Symbol, model.Timeframe, model.Id,
            snap.EnsembleDiversity, maxDiversity, forcedNclLambda, forcedDivLambda);

        // Skip if a full retrain is already queued/running
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
        // so only the fields that differ from the base config need to be specified.
        var overrides = new
        {
            NclLambda      = forcedNclLambda,
            DiversityLambda = forcedDivLambda,
        };

        writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol    = model.Symbol,
            Timeframe = model.Timeframe,
            Status    = RunStatus.Queued,
            HyperparamConfigJson = JsonSerializer.Serialize(new
            {
                triggeredBy      = "MLEnsembleDiversityRecoveryWorker",
                ensembleDiversity = snap.EnsembleDiversity,
                maxDiversity,
                forcedNclLambda,
                forcedDivLambda,
                modelId          = model.Id,
                // Override keys recognised by MLTrainingWorker.LoadHyperparamsAsync
                NclLambda        = forcedNclLambda,
                DiversityLambda  = forcedDivLambda,
            }),
        });

        await writeCtx.SaveChangesAsync(ct);
    }

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
