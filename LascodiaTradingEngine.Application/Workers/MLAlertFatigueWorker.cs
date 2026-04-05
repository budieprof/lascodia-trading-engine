using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects ML alert fatigue by measuring the ratio of acknowledged to total ML alerts
/// over a rolling 7-day window. When the total alert volume is high and the action ratio
/// is low, creates a meta-alert to prompt drift threshold recalibration.
///
/// <para>
/// <b>Alert fatigue detection rules:</b>
/// <list type="bullet">
///   <item>Total ML alerts (AlertType == MLModelDegraded) in the last 7 days > 20</item>
///   <item>Action ratio (acknowledged / total) < 20%</item>
///   <item>When both conditions are met, a meta-alert is created</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Polling interval:</b> configurable via <c>MLAlertFatigue:PollIntervalSeconds</c>
/// (default 86400 s / 24 hours).
/// </para>
/// </summary>
public sealed class MLAlertFatigueWorker : BackgroundService
{
    private const string CK_PollSecs          = "MLAlertFatigue:PollIntervalSeconds";
    private const string CK_WindowDays        = "MLAlertFatigue:WindowDays";
    private const string CK_MinAlerts         = "MLAlertFatigue:MinAlertThreshold";
    private const string CK_MinActionRatio    = "MLAlertFatigue:MinActionRatio";
    private const string CK_AlertDest         = "MLAlertFatigue:AlertDestination";

    private readonly IServiceScopeFactory         _scopeFactory;
    private readonly ILogger<MLAlertFatigueWorker> _logger;

    public MLAlertFatigueWorker(
        IServiceScopeFactory            scopeFactory,
        ILogger<MLAlertFatigueWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLAlertFatigueWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 86400; // default 24 hours

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx      = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs           = await GetConfigAsync<int>(ctx, CK_PollSecs, 86400, stoppingToken);
                int windowDays     = await GetConfigAsync<int>(ctx, CK_WindowDays, 7, stoppingToken);
                int minAlerts      = await GetConfigAsync<int>(ctx, CK_MinAlerts, 20, stoppingToken);
                double minAction   = await GetConfigAsync<double>(ctx, CK_MinActionRatio, 0.20, stoppingToken);
                string alertDest   = await GetConfigAsync<string>(ctx, CK_AlertDest, "ml-ops", stoppingToken);

                var windowStart = DateTime.UtcNow.AddDays(-windowDays);

                // ── Count total ML-related alerts in window ──────────────────
                // Alert entity lacks a CreatedAt column; use LastTriggeredAt as a proxy
                // for when the alert last fired. Alerts that have never fired are included
                // if they were recently created (LastTriggeredAt is null — count them as active).
                var mlAlerts = await ctx.Set<Alert>()
                    .Where(a => a.AlertType == AlertType.MLModelDegraded &&
                                !a.IsDeleted &&
                                (a.LastTriggeredAt == null || a.LastTriggeredAt >= windowStart))
                    .AsNoTracking()
                    .ToListAsync(stoppingToken);

                int totalAlerts = mlAlerts.Count;
                int acknowledgedAlerts = mlAlerts.Count(a => !a.IsActive);
                double actionRatio = totalAlerts > 0
                    ? (double)acknowledgedAlerts / totalAlerts
                    : 1.0;

                // ── Persist fatigue metrics ──────────────────────────────────
                await UpsertConfigAsync(writeCtx, "MLAlertFatigue:TotalAlerts7d",
                    totalAlerts.ToString(), stoppingToken);
                await UpsertConfigAsync(writeCtx, "MLAlertFatigue:AcknowledgedAlerts7d",
                    acknowledgedAlerts.ToString(), stoppingToken);
                await UpsertConfigAsync(writeCtx, "MLAlertFatigue:ActionRatio",
                    actionRatio.ToString("F2"), stoppingToken);

                _logger.LogInformation(
                    "MLAlertFatigue: {Total} ML alerts in {Days}d, {Acked} acknowledged ({Ratio:P0})",
                    totalAlerts, windowDays, acknowledgedAlerts, actionRatio);

                // ── Create meta-alert if fatigue detected ────────────────────
                if (totalAlerts > minAlerts && actionRatio < minAction)
                {
                    var message = $"Alert fatigue detected: {totalAlerts} ML alerts in {windowDays} days, " +
                                  $"only {acknowledgedAlerts} acknowledged ({actionRatio:P0}). " +
                                  "Consider recalibrating drift thresholds.";

                    writeCtx.Set<Alert>().Add(new Alert
                    {
                        Symbol           = "SYSTEM",
                        AlertType        = AlertType.MLModelDegraded,
                        Channel          = AlertChannel.Webhook,
                        Destination      = alertDest,
                        Severity         = AlertSeverity.High,
                        IsActive         = true,
                        ConditionJson    = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            DetectorType      = "AlertFatigue",
                            TotalAlerts       = totalAlerts,
                            AcknowledgedAlerts = acknowledgedAlerts,
                            ActionRatio       = Math.Round(actionRatio, 4),
                            WindowDays        = windowDays,
                        }),
                        DeduplicationKey = "ml-alert-fatigue",
                        CooldownSeconds  = 86400, // 24 hours
                    });
                    await writeCtx.SaveChangesAsync(stoppingToken);

                    _logger.LogWarning(message);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLAlertFatigueWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLAlertFatigueWorker stopping.");
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
