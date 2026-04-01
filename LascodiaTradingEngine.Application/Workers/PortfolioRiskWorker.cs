using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically computes portfolio-level VaR (Value at Risk) and CVaR (Expected Shortfall)
/// for all active trading accounts. Results are cached and used by Tier 2 risk checks
/// to gate new positions when portfolio VaR exceeds configurable limits.
/// </summary>
public class PortfolioRiskWorker : BackgroundService
{
    private readonly ILogger<PortfolioRiskWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PortfolioRiskOptions _options;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private int _consecutiveFailures;

    public PortfolioRiskWorker(
        ILogger<PortfolioRiskWorker> logger,
        IServiceScopeFactory scopeFactory,
        PortfolioRiskOptions options)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _options      = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PortfolioRiskWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ComputePortfolioRiskAsync(stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "PortfolioRiskWorker error (failure #{Count})", _consecutiveFailures);
            }

            var baseInterval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    baseInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : baseInterval;

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task ComputePortfolioRiskAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readContext     = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var riskCalculator = scope.ServiceProvider.GetRequiredService<IPortfolioRiskCalculator>();

        // Query positions once — Position has no TradingAccountId FK, and the EA-based
        // architecture uses a single active account. Positions are shared across accounts.
        var positions = await readContext.GetDbContext()
            .Set<Position>()
            .Where(p => p.Status == Domain.Enums.PositionStatus.Open && !p.IsDeleted)
            .ToListAsync(ct);

        if (positions.Count == 0) return;

        var accounts = await readContext.GetDbContext()
            .Set<TradingAccount>()
            .Where(a => a.IsActive && !a.IsDeleted)
            .ToListAsync(ct);

        foreach (var account in accounts)
        {
            var metrics = await riskCalculator.ComputeAsync(account, positions, ct);

            _logger.LogInformation(
                "PortfolioRisk: account {Id} — VaR95={VaR95:F2}, VaR99={VaR99:F2}, " +
                "CVaR95={CVaR95:F2}, StressedVaR={SVaR:F2}, Concentration={HHI:F4}",
                account.Id, metrics.VaR95, metrics.VaR99,
                metrics.CVaR95, metrics.StressedVaR, metrics.CorrelationConcentration);

            // Alert if VaR exceeds limit
            if (account.Equity > 0 && metrics.VaR95 / account.Equity * 100m > _options.MaxVaR95Pct)
            {
                _logger.LogWarning(
                    "PortfolioRisk: account {Id} VaR95={VaR95:F2} exceeds {Limit}% of equity {Equity:F2}",
                    account.Id, metrics.VaR95, _options.MaxVaR95Pct, account.Equity);
            }
        }
    }
}
