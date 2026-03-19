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
/// from the active broker and syncs it to the active <see cref="TradingAccount"/> record.
/// </summary>
/// <remarks>
/// Runs every 2 minutes. The sync keeps <see cref="TradingAccount.Equity"/>,
/// <see cref="TradingAccount.Balance"/>, <see cref="TradingAccount.MarginUsed"/>, and
/// <see cref="TradingAccount.MarginAvailable"/> up to date without requiring a manual
/// <c>SyncAccountBalanceCommand</c> API call. The broker adapter is queried via
/// <see cref="IBrokerOrderExecutor.GetAccountSummaryAsync"/> to retrieve live figures.
/// </remarks>
public class AccountSyncWorker : BackgroundService
{
    private readonly ILogger<AccountSyncWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(2);

    public AccountSyncWorker(ILogger<AccountSyncWorker> logger, IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

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
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in AccountSyncWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("AccountSyncWorker stopped");
    }

    private async Task SyncActiveAccountAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();
        var broker       = scope.ServiceProvider.GetRequiredService<IBrokerOrderExecutor>();

        var account = await readContext.GetDbContext()
            .Set<TradingAccount>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (account is null)
        {
            _logger.LogDebug("AccountSyncWorker: no active trading account found, skipping");
            return;
        }

        // Fetch live figures from broker
        var summary = await broker.GetAccountSummaryAsync(ct);

        if (summary is null)
        {
            _logger.LogWarning("AccountSyncWorker: broker returned null account summary, skipping sync");
            return;
        }

        await mediator.Send(new SyncAccountBalanceCommand
        {
            Id             = account.Id,
            Balance        = summary.Balance,
            Equity         = summary.Equity,
            MarginUsed     = summary.MarginUsed,
            MarginAvailable = summary.MarginAvailable
        }, ct);

        _logger.LogDebug(
            "AccountSyncWorker: account {AccountId} synced — Balance={Balance:F2}, Equity={Equity:F2}, MarginUsed={Margin:F2}",
            account.Id, summary.Balance, summary.Equity, summary.MarginUsed);
    }
}
