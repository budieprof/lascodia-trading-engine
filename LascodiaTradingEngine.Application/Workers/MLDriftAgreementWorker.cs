using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Application.Services.Alerts;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Tracks cross-detector agreement for ML drift detection. Counts how many independent
/// drift detectors are simultaneously signalling drift for each active model's symbol/timeframe
/// and creates alerts when a consensus threshold is reached.
///
/// <para>
/// Detectors monitored:
/// <list type="number">
///   <item><see cref="MLDriftMonitorWorker"/> — consecutive failure counter > 0</item>
///   <item><see cref="MLAdwinDriftWorker"/> — ADWIN drift flag with future expiry</item>
///   <item><see cref="MLCusumDriftWorker"/> — recent CUSUM alert</item>
///   <item><see cref="MLCovariateShiftWorker"/> — recent covariate shift training run</item>
///   <item><see cref="MLMultiScaleDriftWorker"/> — recent multi-scale drift training run</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Alert rules:</b>
/// <list type="bullet">
///   <item>If >= 4 detectors agree: CRITICAL alert "Multi-detector drift consensus"</item>
///   <item>If 0 detectors agree but model is suppressed: WARNING "Model suppressed but no detectors firing"</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Polling interval:</b> configurable via <c>MLDriftAgreement:PollIntervalSeconds</c> (default 21600 s / 6 hours).
/// </para>
/// </summary>
public sealed class MLDriftAgreementWorker : BackgroundService
{
    private const string CK_PollSecs     = "MLDriftAgreement:PollIntervalSeconds";
    private const string CK_AlertDest    = "MLDriftAgreement:AlertDestination";
    private const string CK_CusumWindowH = "MLDriftAgreement:CusumAlertWindowHours";
    private const string CK_ShiftWindowH = "MLDriftAgreement:ShiftRunWindowHours";

    private readonly IServiceScopeFactory             _scopeFactory;
    private readonly ILogger<MLDriftAgreementWorker>   _logger;

    public MLDriftAgreementWorker(
        IServiceScopeFactory              scopeFactory,
        ILogger<MLDriftAgreementWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLDriftAgreementWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 21600; // default 6 hours

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx      = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 21600, stoppingToken);
                string alertDest = await GetConfigAsync<string>(ctx, CK_AlertDest, "ml-ops", stoppingToken);
                int cusumWindowH = await GetConfigAsync<int>(ctx, CK_CusumWindowH, 24, stoppingToken);
                int shiftWindowH = await GetConfigAsync<int>(ctx, CK_ShiftWindowH, 48, stoppingToken);
                int alertCooldown = await GetConfigAsync<int>(ctx, AlertCooldownDefaults.CK_MLDrift, AlertCooldownDefaults.Default_MLDrift, stoppingToken);

                var activeModels = await ctx.Set<MLModel>()
                    .Where(m => m.IsActive && !m.IsDeleted)
                    .AsNoTracking()
                    .ToListAsync(stoppingToken);

                foreach (var model in activeModels)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await CheckAgreementAsync(
                        model, ctx, writeCtx, alertDest,
                        cusumWindowH, shiftWindowH, alertCooldown, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLDriftAgreementWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLDriftAgreementWorker stopping.");
    }

    /// <summary>
    /// Checks cross-detector agreement for a single model and creates alerts when thresholds are met.
    /// </summary>
    private async Task CheckAgreementAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  alertDest,
        int                                     cusumWindowHours,
        int                                     shiftWindowHours,
        int                                     alertCooldown,
        CancellationToken                       ct)
    {
        var symbol = model.Symbol;
        var tf     = model.Timeframe;
        var now    = DateTime.UtcNow;
        int agreeingDetectors = 0;

        // ── 1. MLDriftMonitorWorker — consecutive failure counter > 0 ────────
        var failKey = $"MLDrift:{symbol}:{tf}:ConsecutiveFailures";
        int failCount = await GetConfigAsync<int>(readCtx, failKey, 0, ct);
        if (failCount > 0) agreeingDetectors++;

        // ── 2. MLAdwinDriftWorker — ADWIN drift flag with future expiry ──────
        var adwinKey = $"MLDrift:{symbol}:{tf}:AdwinDriftDetected";
        string? adwinVal = await GetConfigValueAsync(readCtx, adwinKey, ct);
        if (adwinVal is not null && DateTime.TryParse(adwinVal, out var adwinExpiry) && adwinExpiry > now)
            agreeingDetectors++;

        // ── 3. MLCusumDriftWorker — recent CUSUM alert ───────────────────────
        var cusumCutoff = now.AddHours(-cusumWindowHours);
        // Alert entity lacks a CreatedAt column; use LastTriggeredAt as proxy
        bool recentCusum = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           !a.IsDeleted &&
                           a.ConditionJson.Contains("\"DetectorType\":\"CUSUM\"") &&
                           a.LastTriggeredAt != null &&
                           a.LastTriggeredAt >= cusumCutoff, ct);
        if (recentCusum) agreeingDetectors++;

        // ── 4. MLCovariateShiftWorker — recent covariate shift training run ──
        var shiftCutoff = now.AddHours(-shiftWindowHours);
        bool recentCovariateShift = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == symbol &&
                           r.Timeframe == tf     &&
                           !r.IsDeleted &&
                           r.DriftTriggerType == "CovariateShift" &&
                           r.StartedAt >= shiftCutoff, ct);
        if (recentCovariateShift) agreeingDetectors++;

        // ── 5. MLMultiScaleDriftWorker — recent multi-scale drift training run
        bool recentMultiScale = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == symbol &&
                           r.Timeframe == tf     &&
                           !r.IsDeleted &&
                           r.DriftTriggerType == "MultiSignal" &&
                           r.StartedAt >= shiftCutoff, ct);
        if (recentMultiScale) agreeingDetectors++;

        // ── Persist agreement metric ────────────────────────────────────────
        var agreeKey = $"MLDriftAgreement:{symbol}:{tf}:AgreeingDetectors";
        var checkedKey = $"MLDriftAgreement:{symbol}:{tf}:LastChecked";
        await UpsertConfigAsync(writeCtx, agreeKey, agreeingDetectors.ToString(), ct);
        await UpsertConfigAsync(writeCtx, checkedKey, now.ToString("O"), ct);

        _logger.LogDebug(
            "DriftAgreement {Symbol}/{Tf}: {Count}/5 detectors agreeing (suppressed={Suppressed})",
            symbol, tf, agreeingDetectors, model.IsSuppressed);

        // ── Alert: >= 4 detectors agree — multi-detector drift consensus ────
        if (agreeingDetectors >= 4)
        {
            writeCtx.Set<Alert>().Add(new Alert
            {
                Symbol        = symbol,
                AlertType     = AlertType.MLModelDegraded,
                Severity      = AlertSeverity.Critical,
                IsActive      = true,
                ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    DetectorType      = "DriftAgreement",
                    ModelId           = model.Id,
                    Timeframe         = tf.ToString(),
                    AgreeingDetectors = agreeingDetectors,
                    TotalDetectors    = 5,
                }),
                DeduplicationKey = $"drift-agreement:{symbol}:{tf}",
                CooldownSeconds  = alertCooldown,
            });
            await writeCtx.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Multi-detector drift consensus for {Symbol}/{Tf}: {Count}/5 detectors firing",
                symbol, tf, agreeingDetectors);
        }
        // ── Alert: model suppressed but no detectors firing ─────────────────
        else if (agreeingDetectors == 0 && model.IsSuppressed)
        {
            writeCtx.Set<Alert>().Add(new Alert
            {
                Symbol        = symbol,
                AlertType     = AlertType.MLModelDegraded,
                Severity      = AlertSeverity.High,
                IsActive      = true,
                ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    DetectorType      = "DriftAgreementAnomaly",
                    ModelId           = model.Id,
                    Timeframe         = tf.ToString(),
                    AgreeingDetectors = 0,
                    ModelSuppressed   = true,
                    Message           = "Model suppressed but no detectors firing — potential threshold miscalibration",
                }),
                DeduplicationKey = $"drift-agreement-anomaly:{symbol}:{tf}",
                CooldownSeconds  = alertCooldown * 2,
            });
            await writeCtx.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Model suppressed but no detectors firing for {Symbol}/{Tf} — potential threshold miscalibration",
                symbol, tf);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static async Task<string?> GetConfigValueAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        CancellationToken                       ct)
    {
        return await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key == key)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        int updated = await ctx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Value, value)
                .SetProperty(c => c.LastUpdatedAt, DateTime.UtcNow), ct);

        if (updated == 0)
        {
            ctx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key             = key,
                Value           = value,
                DataType        = ConfigDataType.String,
                IsHotReloadable = true,
                LastUpdatedAt   = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync(ct);
        }
    }
}
