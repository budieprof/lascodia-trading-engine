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
/// The worker runs every 60 seconds. It reads the active <see cref="TradingAccount"/> for
/// current equity, compares it against the highest equity seen since the last
/// <c>DrawdownSnapshot</c> to determine the peak, then dispatches
/// <see cref="RecordDrawdownSnapshotCommand"/> which computes the drawdown percentage and
/// decides the <c>RecoveryMode</c>.
/// </remarks>
public class DrawdownMonitorWorker : BackgroundService
{
    private readonly ILogger<DrawdownMonitorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(60);

    public DrawdownMonitorWorker(ILogger<DrawdownMonitorWorker> logger, IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

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

    private async Task RecordSnapshotAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator      = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Load the active trading account
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

        // Determine running peak from the most recent snapshot
        var latestSnapshot = await readContext.GetDbContext()
            .Set<DrawdownSnapshot>()
            .OrderByDescending(x => x.RecordedAt)
            .FirstOrDefaultAsync(ct);

        decimal peakEquity = latestSnapshot is not null
            ? Math.Max(latestSnapshot.PeakEquity, currentEquity)
            : currentEquity;

        // Guard against zero peak (e.g. account not yet funded)
        if (peakEquity <= 0)
        {
            _logger.LogDebug("DrawdownMonitorWorker: peak equity is zero, skipping snapshot");
            return;
        }

        await mediator.Send(new RecordDrawdownSnapshotCommand
        {
            CurrentEquity = currentEquity,
            PeakEquity    = peakEquity
        }, ct);

        decimal drawdownPct = (peakEquity - currentEquity) / peakEquity * 100m;

        _logger.LogInformation(
            "DrawdownMonitorWorker: snapshot recorded — Equity={Equity:F2}, Peak={Peak:F2}, Drawdown={DD:F2}%",
            currentEquity, peakEquity, drawdownPct);
    }
}
