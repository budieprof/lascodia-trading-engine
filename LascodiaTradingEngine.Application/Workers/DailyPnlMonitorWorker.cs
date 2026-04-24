using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.EmergencyFlatten.Commands.EmergencyFlatten;
using LascodiaTradingEngine.Domain.Entities;
using System.Globalization;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that polls every <see cref="DailyPnlMonitorOptions.PollIntervalSeconds"/>
/// seconds, computes daily P&amp;L for the single active trading account, and dispatches
/// <see cref="EmergencyFlattenCommand"/> when that account's intraday loss exceeds
/// <see cref="TradingAccount.MaxAbsoluteDailyLoss"/>.
///
/// <para>
/// <b>Daily P&amp;L computation:</b> Uses the earliest <see cref="AccountPerformanceAttribution"/>
/// record for today (if available) to determine start-of-day equity, falling back to the most
/// recent prior attribution record's end-of-day equity. If attribution history is absent,
/// it falls back to the nearest account-scoped <see cref="BrokerAccountSnapshot"/> around the
/// configured trading-day rollover boundary. The daily loss is computed as
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
    private readonly TradingDayOptions _tradingDayOptions;
    private readonly ILogger<DailyPnlMonitorWorker> _logger;

    /// <summary>Max backoff delay on consecutive failures (5 minutes).</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private int _consecutiveFailures;

    /// <summary>
    /// EngineConfig key prefix for persisting flattened account dedup state.
    /// Format: "DailyPnlFlatten:{AccountId}" with value = trading-day-start timestamp (round-trip UTC).
    /// This replaces the previous in-memory HashSet which was lost on restart,
    /// allowing the dedup state to survive process restarts within the same trading day.
    /// </summary>
    private const string FlattenConfigKeyPrefix = "DailyPnlFlatten";

    public DailyPnlMonitorWorker(
        IServiceScopeFactory scopeFactory,
        DailyPnlMonitorOptions options,
        TradingDayOptions tradingDayOptions,
        ILogger<DailyPnlMonitorWorker> logger)
    {
        _scopeFactory     = scopeFactory;
        _options          = options;
        _tradingDayOptions = tradingDayOptions;
        _logger           = logger;
    }

    /// <summary>
    /// Runs a single polling cycle. Internal for deterministic unit test access.
    /// </summary>
    internal Task RunCycleAsync(CancellationToken ct) => CheckDailyPnlAsync(ct);

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
    /// Queries the single active trading account, computes daily P&amp;L, and
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

        var activeAccounts = await ctx.Set<TradingAccount>()
            .Where(a => a.IsActive && !a.IsDeleted)
            .AsNoTracking()
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

        if (activeAccounts.Count == 0)
            return;

        if (activeAccounts.Count > 1)
        {
            var cappedAccountIds = activeAccounts
                .Where(a => a.MaxAbsoluteDailyLoss > 0)
                .Select(a => a.Id)
                .ToList();

            if (cappedAccountIds.Count > 0)
            {
                _logger.LogCritical(
                    "DailyPnlMonitorWorker: found {Count} active trading accounts ({AccountIds}) while emergency flatten is global. " +
                    "Skipping cycle until account activation state is repaired.",
                    activeAccounts.Count,
                    string.Join(", ", activeAccounts.Select(a => a.Id)));
            }

            return;
        }

        var account = activeAccounts[0];
        if (account.MaxAbsoluteDailyLoss <= 0)
            return;

        var nowUtc = DateTime.UtcNow;
        var baseline = await TradingDayBoundaryHelper.ResolveStartOfDayEquityAsync(
            ctx,
            account.Id,
            nowUtc,
            _tradingDayOptions,
            ct);
        var tradingDayStartUtc = baseline?.TradingDayStartUtc
            ?? TradingDayBoundaryHelper.GetTradingDayStartUtc(nowUtc, _tradingDayOptions.RolloverMinuteOfDayUtc);
        var tradingDayKey = TradingDayBoundaryHelper.FormatTradingDayKey(tradingDayStartUtc);

        var flattenKey = BuildFlattenKey(account.Id);
        var flattenEntry = await writeCtx.Set<EngineConfig>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Key == flattenKey, ct);
        if (flattenEntry is not null && !flattenEntry.IsDeleted && FlattenMarkerMatchesTradingDay(flattenEntry.Value, tradingDayKey, tradingDayStartUtc))
            return;

        if (baseline is null || baseline.StartOfDayEquity <= 0)
        {
            _logger.LogWarning(
                "DailyPnlMonitorWorker: no trusted start-of-day equity found for account {AccountId} at trading-day start {TradingDayStart:o}; skipping",
                account.Id,
                tradingDayStartUtc);
            return;
        }

        decimal startOfDayEquity = baseline.StartOfDayEquity;

        decimal dailyLoss = startOfDayEquity - account.Equity;
        if (dailyLoss <= 0)
            return;

        _logger.LogDebug(
            "DailyPnlMonitorWorker: account {AccountId} daily loss {Loss:F2} / limit {Limit:F2} (baseline source: {Source})",
            account.Id, dailyLoss, account.MaxAbsoluteDailyLoss, baseline.Source);

        if (dailyLoss < account.MaxAbsoluteDailyLoss)
            return;

        _logger.LogCritical(
            "DailyPnlMonitorWorker: account {AccountId} BREACHED daily loss limit — " +
            "loss={Loss:F2}, limit={Limit:F2}, startEquity={Start:F2}, currentEquity={Current:F2}, source={Source}, tradingDayStart={TradingDayStart:o}",
            account.Id, dailyLoss, account.MaxAbsoluteDailyLoss,
            startOfDayEquity, account.Equity, baseline.Source, tradingDayStartUtc);

        if (!_options.EmergencyFlattenEnabled)
        {
            _logger.LogWarning(
                "DailyPnlMonitorWorker: breach detected for account {AccountId} but emergency flatten is disabled",
                account.Id);
            return;
        }

        await mediator.Send(new EmergencyFlattenCommand
        {
            TriggeredByAccountId = account.Id,
            Reason = $"Daily P&L loss limit breached: loss={dailyLoss:F2} exceeds MaxAbsoluteDailyLoss={account.MaxAbsoluteDailyLoss:F2}"
        }, ct);

        await PersistFlattenMarkerAsync(writeCtx, flattenEntry, tradingDayKey, account.Id, ct);

        _logger.LogCritical(
            "DailyPnlMonitorWorker: EmergencyFlatten dispatched for account {AccountId}",
            account.Id);
    }

    private static string BuildFlattenKey(long accountId) => $"{FlattenConfigKeyPrefix}:{accountId}";

    private static bool FlattenMarkerMatchesTradingDay(
        string? persistedValue,
        string tradingDayKey,
        DateTime tradingDayStartUtc)
    {
        if (string.Equals(persistedValue, tradingDayKey, StringComparison.Ordinal))
            return true;

        // Backward compatibility: older builds persisted yyyy-MM-dd for UTC-midnight trading
        // days. Accept that legacy shape only when the configured trading-day boundary is
        // still midnight UTC so upgrades do not re-trigger a same-day flatten.
        if (tradingDayStartUtc.TimeOfDay != TimeSpan.Zero)
            return false;

        if (!DateTime.TryParseExact(
                persistedValue,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var legacyDayUtc))
        {
            return false;
        }

        return legacyDayUtc.Date == tradingDayStartUtc.Date;
    }

    private static async Task PersistFlattenMarkerAsync(
        DbContext writeCtx,
        EngineConfig? flattenEntry,
        string tradingDayKey,
        long accountId,
        CancellationToken ct)
    {
        var entry = flattenEntry;
        if (entry is not null)
        {
            entry.Value = tradingDayKey;
            entry.Description = $"DailyPnl flatten dedup for account {accountId}";
            entry.DataType = Domain.Enums.ConfigDataType.String;
            entry.IsHotReloadable = false;
            entry.LastUpdatedAt = DateTime.UtcNow;
            entry.IsDeleted = false;
        }
        else
        {
            await writeCtx.Set<EngineConfig>().AddAsync(new EngineConfig
            {
                Key = BuildFlattenKey(accountId),
                Value = tradingDayKey,
                Description = $"DailyPnl flatten dedup for account {accountId}",
                DataType = Domain.Enums.ConfigDataType.String,
                IsHotReloadable = false,
                LastUpdatedAt = DateTime.UtcNow
            }, ct);
        }

        await writeCtx.SaveChangesAsync(ct);
    }
}
