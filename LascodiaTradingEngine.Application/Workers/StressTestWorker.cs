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
            // Filter positions by account affiliation via the opening order's TradingAccountId.
            // Position has no direct TradingAccountId FK — join through OpenOrderId → Order.
            var positions = await readCtx.GetDbContext()
                .Set<Position>()
                .Where(p => p.Status == PositionStatus.Open && !p.IsDeleted
                         && p.OpenOrderId != null
                         && readCtx.GetDbContext().Set<Order>()
                             .Any(o => o.Id == p.OpenOrderId && o.TradingAccountId == account.Id && !o.IsDeleted))
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

            // ── Correlated reverse stress test ──
            // Runs after per-scenario tests if the account has enough positions
            // and a correlation matrix can be computed.
            if (positions.Count >= 3)
            {
                try
                {
                    var correlationAnalyzer = scope.ServiceProvider.GetRequiredService<ICorrelationRiskAnalyzer>();
                    var symbols = positions.Select(p => p.Symbol).Distinct().ToList();
                    const int correlationWindowDays = 60;

                    var pairwiseCorrelations = await correlationAnalyzer.GetCorrelationMatrixAsync(
                        symbols, correlationWindowDays, ct);

                    // Build n x n correlation matrix aligned with positions list
                    int n = positions.Count;
                    var correlationMatrix = new double[n, n];
                    var volatilities = new double[n];
                    bool matrixValid = true;

                    // Compute per-position annualized volatility from daily returns
                    for (int i = 0; i < n; i++)
                    {
                        var closes = await readCtx.GetDbContext()
                            .Set<Candle>()
                            .Where(c => c.Symbol == positions[i].Symbol && c.Timeframe == Timeframe.D1
                                     && c.IsClosed && !c.IsDeleted)
                            .OrderByDescending(c => c.Timestamp)
                            .Take(correlationWindowDays + 1)
                            .OrderBy(c => c.Timestamp)
                            .Select(c => c.Close)
                            .ToListAsync(ct);

                        if (closes.Count < 10)
                        {
                            matrixValid = false;
                            break;
                        }

                        var returns = new List<double>();
                        for (int k = 1; k < closes.Count; k++)
                        {
                            if (closes[k - 1] != 0)
                                returns.Add((double)((closes[k] - closes[k - 1]) / closes[k - 1]));
                        }

                        if (returns.Count < 5)
                        {
                            matrixValid = false;
                            break;
                        }

                        double mean = returns.Average();
                        double variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
                        // Annualize: daily vol * sqrt(252)
                        volatilities[i] = Math.Sqrt(variance) * Math.Sqrt(252.0);
                    }

                    if (matrixValid)
                    {
                        // Fill the n x n matrix from pairwise correlations dictionary
                        for (int i = 0; i < n; i++)
                        {
                            correlationMatrix[i, i] = 1.0;
                            for (int j = i + 1; j < n; j++)
                            {
                                string a = positions[i].Symbol;
                                string b = positions[j].Symbol;
                                string pairKey = string.Compare(a, b, StringComparison.Ordinal) <= 0
                                    ? $"{a}|{b}" : $"{b}|{a}";

                                double corr = 0;
                                if (pairwiseCorrelations.TryGetValue(pairKey, out var corrDecimal))
                                    corr = (double)corrDecimal;

                                correlationMatrix[i, j] = corr;
                                correlationMatrix[j, i] = corr;
                            }
                        }

                        // Default target loss: 25% of equity (configurable in the future)
                        const double targetLossPct = 25.0;

                        var correlatedResult = await stressEngine.RunCorrelatedReverseStressAsync(
                            positions, account, targetLossPct, volatilities, correlationMatrix, ct);

                        correlatedResult.TradingAccountId = account.Id;
                        await writeCtx.GetDbContext().Set<StressTestResult>().AddAsync(correlatedResult, ct);
                        resultCount++;

                        if (correlatedResult.WouldTriggerMarginCall)
                        {
                            _logger.LogCritical(
                                "STRESS TEST: CorrelatedReverse would trigger margin call for account {Account} " +
                                "(P&L={Pnl:F2}, {PnlPct:F1}% of equity, minShock={Shock:F2}%)",
                                account.Id, correlatedResult.StressedPnl, correlatedResult.StressedPnlPct,
                                correlatedResult.MinimumShockPct);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "StressTestWorker: CorrelatedReverse for account {Account}: " +
                                "minShockScale={Shock:F2}%, P&L={Pnl:F2} ({PnlPct:F1}% of equity)",
                                account.Id, correlatedResult.MinimumShockPct,
                                correlatedResult.StressedPnl, correlatedResult.StressedPnlPct);
                        }
                    }
                    else
                    {
                        _logger.LogWarning(
                            "StressTestWorker: insufficient candle data for correlated stress test on account {Account}",
                            account.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "StressTestWorker: failed correlated reverse stress test for account {Account}",
                        account.Id);
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
