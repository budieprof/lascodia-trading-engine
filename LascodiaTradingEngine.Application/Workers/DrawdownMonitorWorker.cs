using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.DrawdownRecovery.Commands.RecordDrawdownSnapshot;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that periodically fetches the active trading account's equity,
/// computes the current drawdown against the running peak, and persists a
/// <see cref="DrawdownSnapshot"/> record. Snapshots form the authoritative equity-curve
/// history used in performance reporting and risk-mode transitions (Normal → Recovery → Halted).
/// </summary>
/// <remarks>
/// <para>
/// <b>Polling interval:</b> 60 seconds (<see cref="PollingInterval"/>). This cadence
/// balances snapshot resolution against write volume; a new row is written to the
/// <c>DrawdownSnapshots</c> table every minute the engine is running.
/// </para>
///
/// <para>
/// <b>Equity curve tracking:</b> The worker maintains a monotonically increasing peak
/// by comparing the current account equity against the <c>PeakEquity</c> field of the
/// most recent persisted snapshot. The peak only ever moves up — drawdown is measured
/// as the percentage decline from the highest equity ever recorded.
/// </para>
///
/// <para>
/// <b>Division of responsibility:</b>
/// <list type="bullet">
///   <item><description>
///     This worker is the <em>data producer</em>: it records snapshots with their
///     computed <c>DrawdownPct</c> and assigns a <c>RecoveryMode</c> to each snapshot
///     via the <see cref="RecordDrawdownSnapshotCommand"/> handler.
///   </description></item>
///   <item><description>
///     <see cref="DrawdownRecoveryWorker"/> is the <em>enforcement layer</em>: it reads
///     the mode from the latest snapshot and takes corrective action (pausing strategies,
///     restricting lot sizes, etc.) when the mode changes.
///   </description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Pipeline position:</b> This worker sits at the start of the drawdown management
/// pipeline. Downstream consumers (<see cref="DrawdownRecoveryWorker"/>, performance
/// attribution queries, the risk dashboard) all read from the snapshot table this
/// worker populates.
/// </para>
/// </remarks>
public class DrawdownMonitorWorker : BackgroundService
{
    private readonly ILogger<DrawdownMonitorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// How often the worker samples account equity and writes a new snapshot.
    /// Set to 60 seconds to give the equity-curve adequate resolution while
    /// keeping DB write volume manageable (≈ 1 440 rows per account per day).
    /// </summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Initialises the worker.
    /// </summary>
    /// <param name="logger">Structured logger for this worker.</param>
    /// <param name="scopeFactory">
    /// Factory used to open a new DI scope on each polling cycle so that scoped
    /// services (<see cref="IReadApplicationDbContext"/>, MediatR) are not shared
    /// across cycles.
    /// </param>
    public DrawdownMonitorWorker(ILogger<DrawdownMonitorWorker> logger, IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs a continuous loop: attempt to record
    /// a snapshot, then wait <see cref="PollingInterval"/> before the next attempt.
    /// Errors inside <see cref="RecordSnapshotAsync"/> are caught here to prevent the
    /// loop from crashing; the next cycle will retry.
    /// </summary>
    /// <param name="stoppingToken">Signalled by the host on application shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DrawdownMonitorWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecordSnapshotAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit the loop cleanly without logging an error.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in DrawdownMonitorWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("DrawdownMonitorWorker stopped");
    }

    /// <summary>
    /// Performs a single snapshot cycle:
    /// <list type="number">
    ///   <item><description>Loads the active <see cref="TradingAccount"/>.</description></item>
    ///   <item><description>
    ///     Reads the most recent <see cref="DrawdownSnapshot"/> to determine the running
    ///     peak equity. The peak is the greater of the stored peak and the current equity,
    ///     ensuring it only ever moves upward.
    ///   </description></item>
    ///   <item><description>
    ///     Dispatches <see cref="RecordDrawdownSnapshotCommand"/> which computes the
    ///     drawdown percentage and assigns the appropriate <c>RecoveryMode</c> before
    ///     persisting the snapshot row.
    ///   </description></item>
    ///   <item><description>Logs the result at Information level for operational visibility.</description></item>
    /// </list>
    /// </summary>
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task RecordSnapshotAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator      = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Load the active trading account — there should be exactly one IsActive account.
        // If none exists (e.g. initial setup) we skip silently at Debug level.
        var account = await readContext.GetDbContext()
            .Set<TradingAccount>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (account is null)
        {
            _logger.LogDebug("DrawdownMonitorWorker: no active trading account found, skipping");
            return;
        }

        decimal currentEquity = account.Equity;

        // Determine running peak from the most recent snapshot.
        // Math.Max ensures the peak can only increase — drawdown is always measured
        // from the highest equity the account has ever achieved.
        var latestSnapshot = await readContext.GetDbContext()
            .Set<DrawdownSnapshot>()
            .OrderByDescending(x => x.RecordedAt)
            .FirstOrDefaultAsync(ct);

        decimal peakEquity = latestSnapshot is not null
            ? Math.Max(latestSnapshot.PeakEquity, currentEquity)
            : currentEquity; // First snapshot ever: peak equals current equity (0 % drawdown)

        // Guard against zero peak — can occur if account is not yet funded or if equity
        // data is stale. Division by zero in the drawdown formula must be prevented.
        if (peakEquity <= 0)
        {
            _logger.LogDebug("DrawdownMonitorWorker: peak equity is zero, skipping snapshot");
            return;
        }

        // Delegate persistence and RecoveryMode assignment to the command handler.
        // The handler applies the thresholds defined in the default RiskProfile to
        // classify the snapshot as Normal, Reduced, or Halted.
        await mediator.Send(new RecordDrawdownSnapshotCommand
        {
            CurrentEquity = currentEquity,
            PeakEquity    = peakEquity
        }, ct);

        // Calculate drawdown % here solely for the log message — the canonical value
        // is computed and stored by the command handler.
        decimal drawdownPct = (peakEquity - currentEquity) / peakEquity * 100m;

        _logger.LogInformation(
            "DrawdownMonitorWorker: snapshot recorded — Equity={Equity:F2}, Peak={Peak:F2}, Drawdown={DD:F2}%",
            currentEquity, peakEquity, drawdownPct);
    }
}
