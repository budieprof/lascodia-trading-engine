using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Runs automated stress tests on a weekly schedule. Iterates all active scenarios
/// against all active accounts and persists results for regulatory reporting.
/// </summary>
public class StressTestWorker : BackgroundService
{
    private readonly ILogger<StressTestWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StressTestOptions _options;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    private int _consecutiveFailures;

    public StressTestWorker(
        ILogger<StressTestWorker> logger,
        IServiceScopeFactory scopeFactory,
        StressTestOptions options)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _options      = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StressTestWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Enabled && ShouldRunNow())
                {
                    await RunAllScenariosAsync(stoppingToken);
                    _consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "StressTestWorker error (failure #{Count})", _consecutiveFailures);
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

    private bool ShouldRunNow()
    {
        var now = DateTime.UtcNow;
        return (int)now.DayOfWeek == _options.WeeklyRunDayOfWeek
            && now.Hour == _options.WeeklyRunHourUtc
            && now.Minute < 10; // 10-minute window
    }

    private async Task RunAllScenariosAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var stressEngine = scope.ServiceProvider.GetRequiredService<IStressTestEngine>();

        var scenarios = await readCtx.GetDbContext()
            .Set<StressTestScenario>()
            .Where(s => s.IsActive && !s.IsDeleted)
            .ToListAsync(ct);

        var accounts = await readCtx.GetDbContext()
            .Set<TradingAccount>()
            .Where(a => a.IsActive && !a.IsDeleted)
            .ToListAsync(ct);

        int resultCount = 0;

        foreach (var account in accounts)
        {
            var positions = await readCtx.GetDbContext()
                .Set<Position>()
                .Where(p => p.Status == PositionStatus.Open && !p.IsDeleted)
                .ToListAsync(ct);

            foreach (var scenario in scenarios)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var result = await stressEngine.RunScenarioAsync(scenario, account, positions, ct);
                    await writeCtx.GetDbContext().Set<StressTestResult>().AddAsync(result, ct);
                    resultCount++;

                    if (result.WouldTriggerMarginCall)
                    {
                        _logger.LogCritical(
                            "STRESS TEST: scenario '{Scenario}' would trigger margin call for account {Account} " +
                            "(P&L={Pnl:F2}, {PnlPct:F1}% of equity)",
                            scenario.Name, account.Id, result.StressedPnl, result.StressedPnlPct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "StressTestWorker: failed to run scenario '{Scenario}' for account {Account}",
                        scenario.Name, account.Id);
                }
            }
        }

        if (resultCount > 0)
        {
            await writeCtx.GetDbContext().SaveChangesAsync(ct);
            _logger.LogInformation("StressTestWorker: completed {Count} stress tests", resultCount);
        }
    }
}
