using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Recalculates portfolio VaR whenever a position is opened (order filled) or closed.
/// Subscribes to both <see cref="OrderFilledIntegrationEvent"/> and
/// <see cref="PositionClosedIntegrationEvent"/> so that VaR is always current after
/// any change to the live position set.
///
/// <para>
/// <b>Why both events?</b> An order fill adds a new position (increasing portfolio risk),
/// while a position close removes one (releasing risk budget). Both events alter the
/// composition of the portfolio and therefore require a VaR refresh.
/// </para>
///
/// <para>
/// <b>DI note:</b> Registered as <c>Transient</c> via <c>AutoRegisterEventHandler</c>
/// and subscribed to the event bus at startup. A new DI scope is created per invocation
/// to safely consume scoped services.
/// </para>
/// </summary>
public sealed class VaRRecalculationEventHandler
    : IIntegrationEventHandler<OrderFilledIntegrationEvent>,
      IIntegrationEventHandler<PositionClosedIntegrationEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VaRRecalculationEventHandler> _logger;

    public VaRRecalculationEventHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<VaRRecalculationEventHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task Handle(OrderFilledIntegrationEvent @event)
    {
        _logger.LogInformation(
            "VaRRecalculationEventHandler: order {OrderId} filled — recalculating portfolio VaR",
            @event.OrderId);
        await RecalculateVaRAsync();
    }

    public async Task Handle(PositionClosedIntegrationEvent @event)
    {
        _logger.LogInformation(
            "VaRRecalculationEventHandler: position {PositionId} closed — recalculating portfolio VaR",
            @event.PositionId);
        await RecalculateVaRAsync();
    }

    /// <summary>
    /// Creates a DI scope, loads all active accounts and their open positions, then
    /// recomputes VaR for each account. Results are logged; downstream consumers
    /// (e.g. risk dashboard) read the metrics on demand via <see cref="IPortfolioRiskCalculator"/>.
    /// </summary>
    private async Task RecalculateVaRAsync()
    {
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var readContext    = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var riskCalculator = scope.ServiceProvider.GetRequiredService<IPortfolioRiskCalculator>();
                var ctx = readContext.GetDbContext();

                var accounts = await ctx.Set<TradingAccount>()
                    .Where(a => a.IsActive && !a.IsDeleted)
                    .AsNoTracking()
                    .ToListAsync();

                // Query positions once — Position has no TradingAccountId FK in this architecture.
                var openPositions = await ctx.Set<Position>()
                    .Where(p => p.Status == PositionStatus.Open && !p.IsDeleted)
                    .AsNoTracking()
                    .ToListAsync();

                if (openPositions.Count == 0) return;

                foreach (var account in accounts)
                {

                    var metrics = await riskCalculator.ComputeAsync(account, openPositions, CancellationToken.None);

                    _logger.LogInformation(
                        "VaRRecalculationEventHandler: account {AccountId} — VaR95={VaR95:F2}, VaR99={VaR99:F2}, " +
                        "CVaR95={CVaR95:F2}, StressedVaR={StressedVaR:F2}, MCVaR95={MCVaR95:F2}",
                        account.Id, metrics.VaR95, metrics.VaR99, metrics.CVaR95,
                        metrics.StressedVaR, metrics.MonteCarloVaR95);
                }

                return; // Success
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                int delayMs = 500 * (int)Math.Pow(2, attempt - 1);
                _logger.LogWarning(ex,
                    "VaRRecalculationEventHandler: error on attempt {Attempt}/{Max} — retrying in {Delay}ms",
                    attempt, maxRetries, delayMs);
                await Task.Delay(delayMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "VaRRecalculationEventHandler: failed after {Max} attempts — VaR may be stale",
                    maxRetries);
            }
        }
    }
}
