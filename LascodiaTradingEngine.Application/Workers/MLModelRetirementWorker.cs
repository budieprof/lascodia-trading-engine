using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Coordinates cross-worker degradation signals to make a holistic model retirement
/// decision, bridging the gap between individual alerting workers and automated action.
///
/// <b>Problem:</b> Each monitoring worker alerts independently on its own metric.
/// A model can accumulate 2–3 simultaneous degradation alerts — EWMA below critical,
/// consecutive-miss cooldown active, rolling accuracy below floor — while technically
/// remaining "active" because no single worker has authority to suppress it.
///
/// <b>Algorithm:</b> Evaluate three independent degradation signals for each model:
/// <list type="number">
///   <item><b>Cooldown active</b> — <c>MLCooldown:{Symbol}:{Tf}:ExpiresAt</c> in
///         <see cref="EngineConfig"/> is a future timestamp.</item>
///   <item><b>EWMA accuracy critical</b> — <see cref="MLModelEwmaAccuracy.EwmaAccuracy"/>
///         is below <c>CriticalEwmaThreshold</c> (default 0.48).</item>
///   <item><b>Live accuracy degraded</b> — <see cref="MLModel.LiveDirectionAccuracy"/>
///         is non-null and below <c>LiveAccuracyFloor</c> (default 0.48).</item>
/// </list>
///
/// When ≥ <c>SignalsRequired</c> (default 2) of these 3 signals are simultaneously
/// active, set <c>MLModel.IsSuppressed = true</c> via <c>ExecuteUpdateAsync</c>
/// and fire a <c>MLModelDecommissioned</c>-reason alert. The model remains suppressed
/// until a new champion is promoted (handled by the shadow-arbiter workflow).
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLRetirement:PollIntervalSeconds</c>     — default 1800 (30 min)</item>
///   <item><c>MLRetirement:CriticalEwmaThreshold</c>   — default 0.48</item>
///   <item><c>MLRetirement:LiveAccuracyFloor</c>        — default 0.48</item>
///   <item><c>MLRetirement:SignalsRequired</c>          — degradation signals needed, default 2</item>
///   <item><c>MLRetirement:AlertDestination</c>         — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLModelRetirementWorker : BackgroundService
{
    private const string CK_PollSecs    = "MLRetirement:PollIntervalSeconds";
    private const string CK_EwmaThr     = "MLRetirement:CriticalEwmaThreshold";
    private const string CK_LiveFloor   = "MLRetirement:LiveAccuracyFloor";
    private const string CK_SigRequired = "MLRetirement:SignalsRequired";
    private const string CK_AlertDest   = "MLRetirement:AlertDestination";

    private readonly IServiceScopeFactory                _scopeFactory;
    private readonly ILogger<MLModelRetirementWorker>    _logger;

    public MLModelRetirementWorker(
        IServiceScopeFactory                 scopeFactory,
        ILogger<MLModelRetirementWorker>     logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLModelRetirementWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 1800;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 1800, stoppingToken);

                await EvaluateAllModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLModelRetirementWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLModelRetirementWorker stopping.");
    }

    // ── Retirement evaluation core ────────────────────────────────────────────

    private async Task EvaluateAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        double ewmaThr      = await GetConfigAsync<double>(readCtx, CK_EwmaThr,     0.48,    ct);
        double liveFloor    = await GetConfigAsync<double>(readCtx, CK_LiveFloor,    0.48,    ct);
        int    sigsRequired = await GetConfigAsync<int>   (readCtx, CK_SigRequired,  2,       ct);
        string alertDest    = await GetConfigAsync<string>(readCtx, CK_AlertDest,    "ml-ops", ct);

        // Load active, non-suppressed models
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsSuppressed && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe, m.LiveDirectionAccuracy })
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await EvaluateModelAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    model.LiveDirectionAccuracy,
                    ewmaThr, liveFloor, sigsRequired, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Retirement: evaluation failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task EvaluateModelAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        decimal?                                liveDirectionAccuracy,
        double                                  ewmaThreshold,
        double                                  liveAccuracyFloor,
        int                                     signalsRequired,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        var now             = DateTime.UtcNow;
        var activeSignals   = new List<string>();

        // ── Signal 1: Consecutive-miss cooldown active ────────────────────────
        var cdKey   = $"MLCooldown:{symbol}:{timeframe}:ExpiresAt";
        var cdEntry = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == cdKey, ct);

        if (cdEntry?.Value is not null &&
            DateTime.TryParse(cdEntry.Value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var cdExpiry) &&
            now < cdExpiry)
        {
            activeSignals.Add($"cooldown_active (expires {cdExpiry:HH:mm} UTC)");
        }

        // ── Signal 2: EWMA accuracy below critical threshold ─────────────────
        var ewmaRow = await readCtx.Set<MLModelEwmaAccuracy>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.MLModelId == modelId, ct);

        if (ewmaRow is not null && ewmaRow.EwmaAccuracy < ewmaThreshold)
            activeSignals.Add($"ewma_critical ({ewmaRow.EwmaAccuracy:P2} < {ewmaThreshold:P2})");

        // ── Signal 3: Live direction accuracy below floor ─────────────────────
        if (liveDirectionAccuracy.HasValue &&
            (double)liveDirectionAccuracy.Value < liveAccuracyFloor)
        {
            activeSignals.Add(
                $"live_accuracy_degraded ({liveDirectionAccuracy.Value:P2} < {liveAccuracyFloor:P2})");
        }

        int signalCount = activeSignals.Count;

        _logger.LogDebug(
            "Retirement: model {Id} ({Symbol}/{Tf}) — {N}/{Required} degradation signals active: {Sigs}",
            modelId, symbol, timeframe, signalCount, signalsRequired,
            string.Join("; ", activeSignals));

        if (signalCount < signalsRequired) return;

        // ── Retire the model ──────────────────────────────────────────────────
        _logger.LogWarning(
            "Retirement: model {Id} ({Symbol}/{Tf}) — {N} simultaneous degradation signals. " +
            "Suppressing model. Signals: {Sigs}",
            modelId, symbol, timeframe, signalCount, string.Join("; ", activeSignals));

        await writeCtx.Set<MLModel>()
            .Where(m => m.Id == modelId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsSuppressed, true), ct);

        // Alert
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                  &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (!alertExists)
        {
            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                Symbol        = symbol,
                Channel       = AlertChannel.Webhook,
                Destination   = alertDest,
                ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    reason             = "model_decommissioned",
                    severity           = "critical",
                    symbol,
                    timeframe          = timeframe.ToString(),
                    modelId,
                    activeSignals,
                    signalCount,
                    signalsRequired,
                    action             = "IsSuppressed set to true — model will no longer score signals",
                }),
                IsActive = true,
            });
        }

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
