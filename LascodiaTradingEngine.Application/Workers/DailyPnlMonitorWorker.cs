using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.EmergencyFlatten.Commands.EmergencyFlatten;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that polls every <see cref="DailyPnlMonitorOptions.PollIntervalSeconds"/>
/// seconds, computes daily P&amp;L for each active trading account, and dispatches
/// <see cref="EmergencyFlattenCommand"/> when any account's intraday loss exceeds
/// <see cref="TradingAccount.MaxAbsoluteDailyLoss"/>.
///
/// <para>
/// <b>Daily P&amp;L computation:</b> Uses the most recent <see cref="AccountPerformanceAttribution"/>
/// record for today (if available) to determine start-of-day equity, falling back to the
/// earliest <see cref="DrawdownSnapshot"/> recorded today. The daily loss is computed as
/// <c>startOfDayEquity - currentEquity</c>.
/// </para>
///
/// <para>
/// <b>Emergency flatten:</b> When the computed daily loss exceeds the account's
/// <c>MaxAbsoluteDailyLoss</c> threshold and <see cref="DailyPnlMonitorOptions.EmergencyFlattenEnabled"/>
/// is true, the worker dispatches an <see cref="EmergencyFlattenCommand"/> which cancels all
/// pending orders, queues close commands for all open positions, and pauses all strategies.
/// </para>
///
/// <para>
/// <b>Resilience:</b> Uses exponential backoff on consecutive failures, capped at 5 minutes.
/// Each polling cycle creates a fresh DI scope to isolate scoped services.
/// </para>
/// </summary>
public class DailyPnlMonitorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DailyPnlMonitorOptions _options;
    private readonly ILogger<DailyPnlMonitorWorker> _logger;

    /// <summary>Max backoff delay on consecutive failures (5 minutes).</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private int _consecutiveFailures;

    /// <summary>
    /// EngineConfig key prefix for persisting flattened account dedup state.
    /// Format: "DailyPnlFlatten:{AccountId}" with value = date string (yyyy-MM-dd).
    /// This replaces the previous in-memory HashSet which was lost on restart,
    /// allowing the dedup state to survive process restarts within the same trading day.
    /// </summary>
    private const string FlattenConfigKeyPrefix = "DailyPnlFlatten";

    public DailyPnlMonitorWorker(
        IServiceScopeFactory scopeFactory,
        DailyPnlMonitorOptions options,
        ILogger<DailyPnlMonitorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options      = options;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DailyPnlMonitorWorker starting (poll interval: {Interval}s, flatten enabled: {Enabled})",
            _options.PollIntervalSeconds, _options.EmergencyFlattenEnabled);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckDailyPnlAsync(stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex,
                    "DailyPnlMonitorWorker: polling error (consecutive failures: {Failures})",
                    _consecutiveFailures);
            }

            // Exponential backoff on consecutive failures
            var baseDelay = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    baseDelay.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : baseDelay;

            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("DailyPnlMonitorWorker stopped");
    }

    /// <summary>
    /// Queries all active trading accounts, computes daily P&amp;L for each, and
    /// triggers emergency flatten if the loss exceeds the account's MaxAbsoluteDailyLoss.
    /// </summary>
    private async Task CheckDailyPnlAsync(CancellationToken ct)
    {
        using var scope     = _scopeFactory.CreateScope();
        var readContext     = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeContext    = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator        = scope.ServiceProvider.GetRequiredService<IMediator>();
        var ctx             = readContext.GetDbContext();
        var writeCtx        = writeContext.GetDbContext();

        var accounts = await ctx.Set<TradingAccount>()
            .Where(a => a.IsActive && !a.IsDeleted && a.MaxAbsoluteDailyLoss > 0)
            .AsNoTracking()
            .ToListAsync(ct);

        if (accounts.Count == 0) return;

        var todayUtc = DateTime.UtcNow.Date;
        var todayStr = todayUtc.ToString("yyyy-MM-dd");

        // Clear stale flatten keys from previous days
        var staleFlattenKeys = await writeCtx.Set<EngineConfig>()
            .Where(c => c.Key.StartsWith(FlattenConfigKeyPrefix + ":") && c.Value != todayStr && !c.IsDeleted)
            .ToListAsync(ct);
        if (staleFlattenKeys.Count > 0)
        {
            foreach (var stale in staleFlattenKeys)
                stale.IsDeleted = true;
            await writeCtx.SaveChangesAsync(ct);
        }

        foreach (var account in accounts)
        {
            // Check EngineConfig for persistent flatten dedup (survives process restart)
            var flattenKey = $"{FlattenConfigKeyPrefix}:{account.Id}";
            var flattenEntry = await ctx.Set<EngineConfig>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == flattenKey && !c.IsDeleted, ct);
            if (flattenEntry is not null && flattenEntry.Value == todayStr)
                continue;

            // Determine start-of-day equity from AccountPerformanceAttribution (preferred)
            // or from the earliest DrawdownSnapshot recorded today (fallback)
            decimal startOfDayEquity = 0;

            var attribution = await ctx.Set<AccountPerformanceAttribution>()
                .Where(a => a.TradingAccountId == account.Id
                         && a.AttributionDate >= todayUtc
                         && !a.IsDeleted)
                .OrderBy(a => a.AttributionDate)
                .FirstOrDefaultAsync(ct);

            if (attribution is not null)
            {
                startOfDayEquity = attribution.StartOfDayEquity;
            }
            else
            {
                // Fallback: earliest snapshot today
                var earliestSnapshot = await ctx.Set<DrawdownSnapshot>()
                    .Where(s => s.RecordedAt >= todayUtc)
                    .OrderBy(s => s.RecordedAt)
                    .FirstOrDefaultAsync(ct);

                if (earliestSnapshot is not null)
                {
                    startOfDayEquity = earliestSnapshot.PeakEquity > 0
                        ? earliestSnapshot.PeakEquity - (earliestSnapshot.PeakEquity * earliestSnapshot.DrawdownPct / 100m)
                        : 0;
                }
            }

            if (startOfDayEquity <= 0)
            {
                _logger.LogDebug(
                    "DailyPnlMonitorWorker: no start-of-day equity for account {AccountId}, skipping",
                    account.Id);
                continue;
            }

            decimal dailyLoss = startOfDayEquity - account.Equity;

            if (dailyLoss <= 0)
                continue; // No loss today

            _logger.LogDebug(
                "DailyPnlMonitorWorker: account {AccountId} daily loss {Loss:F2} / limit {Limit:F2}",
                account.Id, dailyLoss, account.MaxAbsoluteDailyLoss);

            if (dailyLoss >= account.MaxAbsoluteDailyLoss)
            {
                _logger.LogCritical(
                    "DailyPnlMonitorWorker: account {AccountId} BREACHED daily loss limit — " +
                    "loss={Loss:F2}, limit={Limit:F2}, startEquity={Start:F2}, currentEquity={Current:F2}",
                    account.Id, dailyLoss, account.MaxAbsoluteDailyLoss,
                    startOfDayEquity, account.Equity);

                if (_options.EmergencyFlattenEnabled)
                {
                    await mediator.Send(new EmergencyFlattenCommand
                    {
                        TriggeredByAccountId = account.Id,
                        Reason = $"Daily P&L loss limit breached: loss={dailyLoss:F2} exceeds MaxAbsoluteDailyLoss={account.MaxAbsoluteDailyLoss:F2}"
                    }, ct);

                    // Persist flatten dedup to EngineConfig so it survives process restarts
                    var persistKey = $"{FlattenConfigKeyPrefix}:{account.Id}";
                    var existingEntry = await writeCtx.Set<EngineConfig>()
                        .FirstOrDefaultAsync(c => c.Key == persistKey && !c.IsDeleted, ct);
                    if (existingEntry is not null)
                    {
                        existingEntry.Value = todayStr;
                        existingEntry.LastUpdatedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        await writeCtx.Set<EngineConfig>().AddAsync(new EngineConfig
                        {
                            Key = persistKey,
                            Value = todayStr,
                            Description = $"DailyPnl flatten dedup for account {account.Id}",
                            DataType = Domain.Enums.ConfigDataType.String,
                            IsHotReloadable = false,
                            LastUpdatedAt = DateTime.UtcNow
                        }, ct);
                    }
                    await writeCtx.SaveChangesAsync(ct);

                    _logger.LogCritical(
                        "DailyPnlMonitorWorker: EmergencyFlatten dispatched for account {AccountId}",
                        account.Id);
                }
            }
        }
    }
}
