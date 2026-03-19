using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that monitors risk exposure every 30 seconds,
/// logging warnings when open position counts approach the configured limit.
/// </summary>
public class RiskMonitorWorker : BackgroundService
{
    private readonly ILogger<RiskMonitorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public RiskMonitorWorker(
        ILogger<RiskMonitorWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RiskMonitorWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            await MonitorRiskAsync(stoppingToken);
        }

        _logger.LogInformation("RiskMonitorWorker stopped");
    }

    private async Task MonitorRiskAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context     = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

            var defaultProfile = await context.GetDbContext()
                .Set<Domain.Entities.RiskProfile>()
                .FirstOrDefaultAsync(x => x.IsDefault && !x.IsDeleted, cancellationToken);

            if (defaultProfile == null)
            {
                _logger.LogWarning("RiskMonitorWorker: no default risk profile configured");
                return;
            }

            int openPositionCount = await context.GetDbContext()
                .Set<Domain.Entities.Position>()
                .CountAsync(x => x.Status == PositionStatus.Open && !x.IsDeleted, cancellationToken);

            if (openPositionCount >= defaultProfile.MaxOpenPositions)
            {
                _logger.LogWarning(
                    "RiskMonitorWorker: open position count {Count} has reached the limit of {Max} set by profile '{Profile}'",
                    openPositionCount, defaultProfile.MaxOpenPositions, defaultProfile.Name);
            }
            else if (openPositionCount >= defaultProfile.MaxOpenPositions * 0.8)
            {
                _logger.LogWarning(
                    "RiskMonitorWorker: open position count {Count} is approaching the limit of {Max} (profile: '{Profile}')",
                    openPositionCount, defaultProfile.MaxOpenPositions, defaultProfile.Name);
            }
            else
            {
                _logger.LogDebug(
                    "RiskMonitorWorker: {Count}/{Max} open positions (profile: '{Profile}')",
                    openPositionCount, defaultProfile.MaxOpenPositions, defaultProfile.Name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "RiskMonitorWorker error during risk monitoring cycle");
        }
    }
}
