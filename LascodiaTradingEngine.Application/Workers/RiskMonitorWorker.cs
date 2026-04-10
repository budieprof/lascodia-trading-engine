using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that monitors portfolio-level risk exposure every 30 seconds
/// by comparing the current count of open positions against the limit defined in the
/// default <see cref="Domain.Entities.RiskProfile"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Polling interval:</b> 30 seconds (hard-coded). This keeps the monitoring
/// overhead low while still detecting rapid position accumulation within a single
/// trading minute.
/// </para>
///
/// <para>
/// <b>What is monitored:</b>
/// <list type="bullet">
///   <item><description>
///     The count of <see cref="Domain.Entities.Position"/> records in the
///     <see cref="PositionStatus.Open"/> state is fetched from the read-only DB context
///     on every cycle.
///   </description></item>
///   <item><description>
///     The count is compared against <c>MaxOpenPositions</c> from the default
///     <see cref="Domain.Entities.RiskProfile"/> (the one marked <c>IsDefault = true</c>).
///   </description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Warning thresholds:</b>
/// <list type="bullet">
///   <item><description>
///     <b>≥ 100 % of limit</b> — <c>LogWarning</c>: limit reached; no new positions should
///     be opened (the <see cref="IRiskChecker"/> enforced by command handlers is the hard
///     gate; this worker provides a secondary observability signal).
///   </description></item>
///   <item><description>
///     <b>≥ 80 % of limit</b> — <c>LogWarning</c>: approaching limit; operator should
///     be prepared to intervene or let natural trade closure bring the count down.
///   </description></item>
///   <item><description>
///     <b>&lt; 80 % of limit</b> — <c>LogDebug</c>: healthy headroom; logged only at
///     debug verbosity to avoid log noise in production.
///   </description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Pipeline position:</b> This worker is a passive observer. It does not modify any
/// entity state. Actual enforcement of the position limit is performed by
/// <see cref="RiskProfiles.Services.RiskChecker"/> inside the
/// <c>OpenPositionCommandHandler</c> pipeline before a position is ever created.
/// The worker exists to catch drift (e.g. positions opened directly at the broker)
/// and to surface early warnings to operations teams via structured logs / alerting.
/// </para>
///
/// <para>
/// <b>Scope management:</b> A fresh <see cref="IServiceScope"/> is created on every
/// polling cycle so that the scoped <see cref="IReadApplicationDbContext"/> is
/// disposed promptly and does not accumulate a stale EF change-tracker between cycles.
/// </para>
/// </remarks>
public class RiskMonitorWorker : BackgroundService
{
    private readonly ILogger<RiskMonitorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private const int DefaultPollSeconds = 30;
    private const int MaxBackoffSeconds = 300;
    private const string CK_WarningThresholdPct = "RiskMonitor:WarningThresholdPct";
    private int _consecutiveFailures;

    /// <summary>
    /// Initialises the worker with the logger and a scope factory.
    /// The scope factory is used instead of injecting scoped services directly
    /// because <see cref="BackgroundService"/> is registered as a Singleton.
    /// </summary>
    /// <param name="logger">Structured logger for this worker.</param>
    /// <param name="scopeFactory">
    /// Factory used to create a new DI scope on each polling cycle, ensuring that
    /// the scoped <see cref="IReadApplicationDbContext"/> is not shared across cycles.
    /// </param>
    public RiskMonitorWorker(
        ILogger<RiskMonitorWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs an infinite poll loop until the
    /// host requests cancellation. The delay is taken <em>before</em> the first
    /// check so that startup noise (positions being restored from DB) settles first.
    /// </summary>
    /// <param name="stoppingToken">
    /// Signalled by the host when the application is shutting down. The
    /// <see cref="Task.Delay"/> call will throw <see cref="OperationCanceledException"/>
    /// which unwinds the loop cleanly.
    /// </param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RiskMonitorWorker starting");

        // Wait first — gives the rest of the engine time to fully start
        await Task.Delay(TimeSpan.FromSeconds(DefaultPollSeconds), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorRiskAsync(stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                int backoffSecs = Math.Min(
                    DefaultPollSeconds * (int)Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoffSeconds);
                _logger.LogError(ex,
                    "RiskMonitorWorker: error in monitoring cycle (consecutive={Count}), backing off {Backoff}s",
                    _consecutiveFailures, backoffSecs);
                await Task.Delay(TimeSpan.FromSeconds(backoffSecs), stoppingToken);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(DefaultPollSeconds), stoppingToken);
        }

        _logger.LogInformation("RiskMonitorWorker stopped");
    }

    /// <summary>
    /// Executes a single risk-monitoring cycle:
    /// <list type="number">
    ///   <item><description>Loads the default <see cref="Domain.Entities.RiskProfile"/>.</description></item>
    ///   <item><description>Counts all open <see cref="Domain.Entities.Position"/> records.</description></item>
    ///   <item><description>Emits a structured log entry at the appropriate severity level.</description></item>
    /// </list>
    /// Exceptions are caught and logged so a transient DB failure does not crash the
    /// background worker; the next cycle will retry automatically.
    /// </summary>
    /// <param name="cancellationToken">Propagated from the host stopping token.</param>
    private async Task MonitorRiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Create a fresh scope so the EF DbContext is not reused across cycles,
            // which could cause stale query results due to first-level cache retention.
            using var scope = _scopeFactory.CreateScope();
            var context     = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

            // Fetch the default risk profile — this is the engine-wide position limit.
            // If no default profile is configured the check cannot proceed; log and exit early.
            var defaultProfile = await context.GetDbContext()
                .Set<Domain.Entities.RiskProfile>()
                .FirstOrDefaultAsync(x => x.IsDefault && !x.IsDeleted, cancellationToken);

            if (defaultProfile == null)
            {
                _logger.LogWarning("RiskMonitorWorker: no default risk profile configured");
                return;
            }

            // Count positions currently open across all strategies and accounts.
            // The global query filter on IsDeleted is applied automatically by EF.
            int openPositionCount = await context.GetDbContext()
                .Set<Domain.Entities.Position>()
                .CountAsync(x => x.Status == PositionStatus.Open && !x.IsDeleted, cancellationToken);

            // Read the warning threshold from EngineConfig (hot-reloadable, default 80%).
            double warningThreshold = await GetConfigAsync(context, CK_WarningThresholdPct, 0.80);

            if (openPositionCount >= defaultProfile.MaxOpenPositions)
            {
                // Hard limit reached — the engine should be blocking new positions via
                // RiskChecker, but this log entry flags any bypass for audit purposes.
                _logger.LogWarning(
                    "RiskMonitorWorker: open position count {Count} has reached the limit of {Max} set by profile '{Profile}'",
                    openPositionCount, defaultProfile.MaxOpenPositions, defaultProfile.Name);
            }
            else if (openPositionCount >= defaultProfile.MaxOpenPositions * warningThreshold)
            {
                // Configurable threshold — early warning so operators can act before the hard limit.
                // Adjust via EngineConfig key "RiskMonitor:WarningThresholdPct" (e.g. 0.80 = 80%).
                _logger.LogWarning(
                    "RiskMonitorWorker: open position count {Count} is approaching the limit of {Max} (threshold: {Threshold:P0}, profile: '{Profile}')",
                    openPositionCount, defaultProfile.MaxOpenPositions, warningThreshold, defaultProfile.Name);
            }
            else
            {
                // Healthy headroom — debug level to keep production logs quiet.
                _logger.LogDebug(
                    "RiskMonitorWorker: {Count}/{Max} open positions (profile: '{Profile}')",
                    openPositionCount, defaultProfile.MaxOpenPositions, defaultProfile.Name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Re-throw to the outer loop which handles exponential backoff.
            _logger.LogError(ex, "RiskMonitorWorker error during risk monitoring cycle");
            throw;
        }
    }

    /// <summary>
    /// Reads a typed value from <see cref="Domain.Entities.EngineConfig"/> by key,
    /// falling back to <paramref name="defaultValue"/> if the key is missing or
    /// the stored value cannot be converted.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        IReadApplicationDbContext readContext, string key, T defaultValue)
    {
        var entry = await readContext.GetDbContext()
            .Set<Domain.Entities.EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
