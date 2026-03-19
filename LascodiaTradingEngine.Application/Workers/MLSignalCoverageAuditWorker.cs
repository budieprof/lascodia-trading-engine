using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects silent ML scorer failures by auditing whether approved <see cref="TradeSignal"/>
/// records produced corresponding <see cref="MLModelPredictionLog"/> entries.
///
/// <b>Problem:</b> If <c>MLSignalScorer</c> throws an exception that is caught and swallowed
/// by the calling strategy worker, signals flow through to order execution without any ML
/// evaluation. Aggregate accuracy metrics won't detect this because fewer logs are written —
/// the denominator shrinks silently alongside the numerator.
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>For each active model (symbol/timeframe), count <see cref="TradeSignal"/> records
///         with <c>Status = Approved</c> in the audit window.</item>
///   <item>Count <see cref="MLModelPredictionLog"/> records created in the same window for
///         the same symbol, regardless of model (covers the case where a model was swapped
///         mid-window).</item>
///   <item>If <c>uncoveredFraction = (signals − logs) / signals &gt; CoverageGapThreshold</c>,
///         fire an <see cref="AlertType.MLModelDegraded"/> alert.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLCoverageAudit:PollIntervalSeconds</c>  — default 86400 (24 h)</item>
///   <item><c>MLCoverageAudit:AuditWindowHours</c>     — look-back window, default 24 h</item>
///   <item><c>MLCoverageAudit:MinSignals</c>           — minimum signals before auditing, default 5</item>
///   <item><c>MLCoverageAudit:CoverageGapThreshold</c> — max tolerated gap fraction, default 0.20</item>
///   <item><c>MLCoverageAudit:AlertDestination</c>     — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLSignalCoverageAuditWorker : BackgroundService
{
    private const string CK_PollSecs      = "MLCoverageAudit:PollIntervalSeconds";
    private const string CK_WindowHours   = "MLCoverageAudit:AuditWindowHours";
    private const string CK_MinSignals    = "MLCoverageAudit:MinSignals";
    private const string CK_GapThreshold  = "MLCoverageAudit:CoverageGapThreshold";
    private const string CK_AlertDest     = "MLCoverageAudit:AlertDestination";

    private readonly IServiceScopeFactory                   _scopeFactory;
    private readonly ILogger<MLSignalCoverageAuditWorker>   _logger;

    public MLSignalCoverageAuditWorker(
        IServiceScopeFactory                  scopeFactory,
        ILogger<MLSignalCoverageAuditWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSignalCoverageAuditWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 86400;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 86400, stoppingToken);

                await AuditCoverageAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLSignalCoverageAuditWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLSignalCoverageAuditWorker stopping.");
    }

    // ── Audit core ────────────────────────────────────────────────────────────

    private async Task AuditCoverageAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowHours   = await GetConfigAsync<int>   (readCtx, CK_WindowHours,  24,      ct);
        int    minSignals    = await GetConfigAsync<int>   (readCtx, CK_MinSignals,   5,       ct);
        double gapThreshold  = await GetConfigAsync<double>(readCtx, CK_GapThreshold, 0.20,    ct);
        string alertDest     = await GetConfigAsync<string>(readCtx, CK_AlertDest,   "ml-ops", ct);

        var cutoff = DateTime.UtcNow.AddHours(-windowHours);

        // ── Load distinct symbol/timeframe pairs from active models ──────────
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);

        if (activeModels.Count == 0) return;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await AuditModelCoverageAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    cutoff, minSignals, gapThreshold, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CoverageAudit: check failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task AuditModelCoverageAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        DateTime                                cutoff,
        int                                     minSignals,
        double                                  gapThreshold,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Count approved signals in the window for this symbol
        // (TradeSignal doesn't have a Timeframe field, so we audit at symbol level)
        int approvedSignals = await readCtx.Set<TradeSignal>()
            .CountAsync(ts => ts.Symbol == symbol            &&
                              ts.Status == TradeSignalStatus.Approved &&
                              ts.GeneratedAt >= cutoff       &&
                              !ts.IsDeleted, ct);

        if (approvedSignals < minSignals)
        {
            _logger.LogDebug(
                "CoverageAudit: {Symbol}/{Tf} model {Id} — only {N} approved signals in window, skipping audit.",
                symbol, timeframe, modelId, approvedSignals);
            return;
        }

        // Count prediction logs created in the same window for this model.
        // We use MLModelId (not symbol) so we correctly attribute logs to the active model.
        int predictionLogs = await readCtx.Set<MLModelPredictionLog>()
            .CountAsync(l => l.MLModelId == modelId &&
                             l.PredictedAt >= cutoff  &&
                             !l.IsDeleted, ct);

        int    uncovered       = Math.Max(0, approvedSignals - predictionLogs);
        double uncoveredFrac   = approvedSignals > 0 ? (double)uncovered / approvedSignals : 0.0;

        _logger.LogDebug(
            "CoverageAudit: {Symbol}/{Tf} model {Id} — signals={Sig} logs={Logs} uncovered={Unc} ({Pct:P1})",
            symbol, timeframe, modelId, approvedSignals, predictionLogs, uncovered, uncoveredFrac);

        if (uncoveredFrac <= gapThreshold) return;

        _logger.LogWarning(
            "CoverageAudit: {Symbol}/{Tf} model {Id} — {Pct:P1} of {Sig} approved signals have no " +
            "prediction log (threshold {Thr:P0}). Possible scorer failure.",
            symbol, timeframe, modelId, uncoveredFrac, approvedSignals, gapThreshold);

        // Deduplicate: only create an alert if one doesn't already exist
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                  &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.MLModelDegraded,
            Symbol        = symbol,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason          = "coverage_gap",
                severity        = "warning",
                symbol,
                timeframe       = timeframe.ToString(),
                modelId,
                approvedSignals,
                predictionLogs,
                uncoveredFraction = uncoveredFrac,
                windowHours     = (int)(DateTime.UtcNow - cutoff).TotalHours,
            }),
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
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
