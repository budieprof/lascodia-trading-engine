using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.SyncAccountBalance;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that periodically fetches live account balance and margin data
/// from the active broker and syncs it to the active <see cref="TradingAccount"/> record
/// in the database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Polling interval:</b> 2 minutes (<see cref="PollingInterval"/>). This cadence
/// ensures that equity and margin figures used by risk checks and the drawdown monitor
/// are never more than ~2 minutes stale, while keeping the broker API call rate
/// well within typical rate limits.
/// </para>
///
/// <para>
/// <b>What is synced:</b> The worker updates the following fields on the active
/// <see cref="TradingAccount"/>:
/// <list type="bullet">
///   <item><description><c>Balance</c> — settled cash balance (closed trade P&amp;L included)</description></item>
///   <item><description><c>Equity</c> — balance plus unrealised P&amp;L of open positions</description></item>
///   <item><description><c>MarginUsed</c> — collateral currently locked by open positions</description></item>
///   <item><description><c>MarginAvailable</c> — free margin available for new positions</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Why this matters in the pipeline:</b>
/// <see cref="DrawdownMonitorWorker"/> reads <c>TradingAccount.Equity</c> to compute
/// the drawdown percentage. If the equity figure is stale, the drawdown calculation
/// will be incorrect. This worker ensures the equity value is refreshed every 2 minutes
/// so the drawdown pipeline works from current data.
/// </para>
///
/// <para>
/// <b>Broker interaction:</b> The active <see cref="IBrokerOrderExecutor"/> is resolved
/// from the DI scope, which means the <see cref="BrokerAdapters.BrokerFailoverService"/>
/// can transparently redirect the call to a backup broker if the primary is unavailable.
/// A <c>null</c> summary response (e.g. broker offline) causes the sync to be skipped
/// and logged as a warning; the next cycle will retry.
/// </para>
///
/// <para>
/// <b>Command delegation:</b> The actual DB write is performed by
/// <see cref="SyncAccountBalanceCommand"/> via MediatR so that the standard validation
/// and pipeline behaviours (logging, exception handling) apply consistently.
/// </para>
/// </remarks>
public class AccountSyncWorker : BackgroundService
{
    private readonly ILogger<AccountSyncWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// How often the worker fetches account data from the broker (2 minutes).
    /// Chosen to balance data freshness against broker API rate limits.
    /// </summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Initialises the worker.
    /// </summary>
    /// <param name="logger">Structured logger.</param>
    /// <param name="scopeFactory">
    /// Factory for creating per-cycle DI scopes. Required because
    /// <see cref="IBrokerOrderExecutor"/> and <see cref="IReadApplicationDbContext"/>
    /// are Scoped services that must not be shared across polling cycles.
    /// </param>
    public AccountSyncWorker(ILogger<AccountSyncWorker> logger, IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs an infinite poll loop, calling
    /// <see cref="SyncActiveAccountAsync"/> on each iteration. Errors are caught and logged
    /// so a transient broker outage does not crash the worker; the next 2-minute cycle
    /// will retry automatically.
    /// </summary>
    /// <param name="stoppingToken">Signalled by the host on shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AccountSyncWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncActiveAccountAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit the loop cleanly.
                break;
            }
            catch (Exception ex)
            {
                // Swallow non-cancellation exceptions to keep the worker alive through
                // transient broker connectivity issues or DB errors.
                _logger.LogError(ex, "Unexpected error in AccountSyncWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("AccountSyncWorker stopped");
    }

    /// <summary>
    /// Performs a single account sync cycle:
    /// <list type="number">
    ///   <item><description>Locates the single active, non-deleted <see cref="TradingAccount"/>.</description></item>
    ///   <item><description>
    ///     Calls <see cref="IBrokerOrderExecutor.GetAccountSummaryAsync"/> to retrieve
    ///     live balance, equity, and margin figures from the broker.
    ///   </description></item>
    ///   <item><description>
    ///     Dispatches <see cref="SyncAccountBalanceCommand"/> to write the updated figures
    ///     to the database via MediatR (so validation and pipeline behaviours apply).
    ///   </description></item>
    /// </list>
    ///
    /// Early-exit conditions (both logged at appropriate severity):
    /// <list type="bullet">
    ///   <item><description>No active trading account found — Debug level (expected during initial setup).</description></item>
    ///   <item><description>Broker returned null summary — Warning level (indicates connectivity issue).</description></item>
    /// </list>
    /// </summary>
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task SyncActiveAccountAsync(CancellationToken ct)
    {
        // Fresh scope per cycle — scoped services are isolated and disposed promptly.
        using var scope  = _scopeFactory.CreateScope();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Resolve the broker executor — may be the primary or failover adapter depending
        // on BrokerFailoverService's current state.
        var broker       = scope.ServiceProvider.GetRequiredService<IBrokerOrderExecutor>();

        // There should be exactly one IsActive account at any time; if none exists
        // (e.g. during initial configuration) we skip silently at Debug level.
        var account = await readContext.GetDbContext()
            .Set<TradingAccount>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (account is null)
        {
            _logger.LogDebug("AccountSyncWorker: no active trading account found, skipping");
            return;
        }

        // Fetch live figures from the broker. A null response typically indicates the broker
        // API is temporarily unavailable (network issue, maintenance window, or rate limit).
        var summary = await broker.GetAccountSummaryAsync(ct);

        if (summary is null)
        {
            // Do not update stale data with nothing — skip this cycle and retry next time.
            _logger.LogWarning("AccountSyncWorker: broker returned null account summary, skipping sync");
            return;
        }

        // Delegate the DB write to the command handler to ensure the standard MediatR
        // pipeline (validation, performance logging, exception handling) is applied.
        await mediator.Send(new SyncAccountBalanceCommand
        {
            Id              = account.Id,
            Balance         = summary.Balance,
            Equity          = summary.Equity,
            MarginUsed      = summary.MarginUsed,
            MarginAvailable = summary.MarginAvailable
        }, ct);

        // Log at Debug to avoid noisy Info-level output every 2 minutes in production.
        // Change to Information if real-time account monitoring in logs is needed.
        _logger.LogDebug(
            "AccountSyncWorker: account {AccountId} synced — Balance={Balance:F2}, Equity={Equity:F2}, MarginUsed={Margin:F2}",
            account.Id, summary.Balance, summary.Equity, summary.MarginUsed);
    }
}
