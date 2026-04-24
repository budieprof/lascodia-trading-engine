using System.Globalization;
using System.Threading;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.DrawdownRecovery.Commands.RecordDrawdownSnapshot;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Hybrid polling + event-driven worker that monitors account equity and drawdown.
///
/// <b>Polling mode:</b> Records a drawdown snapshot every 60 seconds (configurable) by
/// comparing current equity against the running peak.
///
/// <b>Event-driven mode:</b> Subscribes to <see cref="PositionClosedIntegrationEvent"/>.
/// When a position closes with a loss exceeding the <c>Drawdown:EmergencyLossPct</c>
/// threshold (default 2% of equity), an immediate out-of-cycle snapshot is recorded.
/// This catches fast drawdowns in volatile markets that could exceed limits between
/// regular polling intervals.
///
/// <b>Division of responsibility:</b>
/// This worker is the <em>data producer</em>: it records snapshots with their computed
/// <c>DrawdownPct</c> and assigns a <c>RecoveryMode</c> via the
/// <see cref="RecordDrawdownSnapshotCommand"/> handler. <see cref="DrawdownRecoveryWorker"/>
/// is the <em>enforcement layer</em> that reads the mode and takes corrective action.
/// </summary>
public class DrawdownMonitorWorker : BackgroundService, IIntegrationEventHandler<PositionClosedIntegrationEvent>
{
    internal const string WorkerName = nameof(DrawdownMonitorWorker);

    private readonly ILogger<DrawdownMonitorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    private const string CK_PollSecs            = "Drawdown:PollIntervalSeconds";
    private const string CK_PollSecsLegacy      = "DrawdownMonitor:IntervalSeconds";
    private const string CK_EmergencyLossPct    = "Drawdown:EmergencyLossPct";
    private const string CK_CriticalMarginPct   = "Drawdown:CriticalMarginPct";
    private const string CK_CautionMarginPct    = "Drawdown:CautionMarginPct";
    private const int DefaultPollSeconds        = 60;
    private const decimal DefaultEmergencyPct   = 2.0m; // 2% of equity triggers emergency snapshot
    private const decimal DefaultCriticalMarginPct = 150.0m;
    private const decimal DefaultCautionMarginPct  = 200.0m;

    /// <summary>Max backoff delay on consecutive failures (5 minutes).</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>Cooldown to prevent flooding from rapid position closures.</summary>
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private DateTimeOffset _lastEmergencySnapshotAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan EmergencyCooldown = TimeSpan.FromSeconds(10);

    private int _consecutiveFailures;
    private bool _legacyPollIntervalWarningEmitted;

    public DrawdownMonitorWorker(
        ILogger<DrawdownMonitorWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics,
        TimeProvider? timeProvider = null)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _metrics      = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Polling mode — regular scheduled snapshots
    // ═══════════════════════════════════════════════════════════════════════

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} starting (hybrid: polling + event-driven)", WorkerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSeconds;
            try
            {
                await RunCycleAsync(stoppingToken);
                _consecutiveFailures = 0;
                pollSecs = await GetPollIntervalSecondsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex,
                    "{Worker}: polling error (consecutive failures: {Failures})",
                    WorkerName,
                    _consecutiveFailures);
            }

            var pollingInterval = TimeSpan.FromSeconds(pollSecs);

            // Exponential backoff on consecutive failures: 60s, 120s, 240s, capped at 5min
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    pollingInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : pollingInterval;

            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("{Worker} stopped", WorkerName);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Event-driven mode — emergency snapshot on large loss
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by the event bus when a position is closed. If the realized loss exceeds the
    /// configurable emergency threshold (% of equity), triggers an immediate drawdown snapshot
    /// outside the normal polling cycle. This catches fast drawdowns that could exceed limits
    /// between regular 60-second snapshots.
    /// </summary>
    public async Task Handle(PositionClosedIntegrationEvent @event)
    {
        // Only react to losses
        if (@event.WasProfitable || @event.RealisedPnL >= 0)
            return;

        bool lockAcquired = false;
        try
        {
            await _snapshotLock.WaitAsync();
            lockAcquired = true;

            var now = _timeProvider.GetUtcNow();

            // Cooldown: don't flood snapshots from rapid consecutive closures
            if ((now - _lastEmergencySnapshotAt) < EmergencyCooldown)
                return;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var mediator    = scope.ServiceProvider.GetRequiredService<IMediator>();
            var account     = await GetSingleActiveAccountAsync(
                readContext.GetDbContext(),
                operationName: "emergency snapshot",
                CancellationToken.None);

            if (account is null || account.Equity <= 0)
                return;

            decimal emergencyPct = await GetDecimalConfigAsync(
                readContext,
                CK_EmergencyLossPct,
                DefaultEmergencyPct,
                CancellationToken.None);

            decimal lossPct = Math.Abs(@event.RealisedPnL) / account.Equity * 100m;
            if (lossPct < emergencyPct)
                return; // Loss is small relative to equity — normal polling will catch it

            _logger.LogWarning(
                "{Worker}: emergency snapshot triggered — position {PositionId} ({Symbol}) " +
                "closed with {LossPct:F2}% loss ({PnL:F2}) exceeding {Threshold:F1}% threshold",
                WorkerName,
                @event.PositionId,
                @event.Symbol,
                lossPct,
                @event.RealisedPnL,
                emergencyPct);

            if (!await RecordSnapshotCoreAsync(readContext, mediator, account, CancellationToken.None))
                return;

            _lastEmergencySnapshotAt = _timeProvider.GetUtcNow();
            _metrics.DrawdownEmergencySnapshots.Add(1,
                new KeyValuePair<string, object?>("worker", WorkerName),
                new KeyValuePair<string, object?>("trigger", "position_closed_loss"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Worker}: failed to record emergency snapshot after position {PositionId} closure",
                WorkerName,
                @event.PositionId);
        }
        finally
        {
            if (lockAcquired)
                _snapshotLock.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Shared snapshot logic
    // ═══════════════════════════════════════════════════════════════════════

    internal Task RunCycleAsync(CancellationToken ct) => RecordSnapshotAsync(ct);
    internal Task<int> ReadPollIntervalSecondsAsync(CancellationToken ct) => GetPollIntervalSecondsAsync(ct);

    private async Task RecordSnapshotAsync(CancellationToken ct)
    {
        bool lockAcquired = false;
        try
        {
            await _snapshotLock.WaitAsync(ct);
            lockAcquired = true;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var mediator    = scope.ServiceProvider.GetRequiredService<IMediator>();
            var account     = await GetSingleActiveAccountAsync(
                readContext.GetDbContext(),
                operationName: "polling snapshot",
                ct);

            if (account is null)
                return;

            await RecordSnapshotCoreAsync(readContext, mediator, account, ct);
        }
        finally
        {
            if (lockAcquired)
                _snapshotLock.Release();
        }
    }

    private async Task<bool> RecordSnapshotCoreAsync(
        IReadApplicationDbContext readContext,
        IMediator mediator,
        TradingAccount account,
        CancellationToken ct)
    {
        var db = readContext.GetDbContext();

        decimal currentEquity = account.Equity;

        // Zero/negative equity is virtually always a stale or stub account reading —
        // real drawdowns approach zero but don't hit exactly 0.00000000. Recording a
        // snapshot with a zero currentEquity against a positive historical peakEquity
        // synthesises DrawdownPct=100.00%, which trips the Halted threshold and causes
        // DrawdownRecoveryWorker to pause every active strategy on bogus data. Skip
        // the snapshot entirely until a real equity value arrives.
        if (currentEquity <= 0)
        {
            _logger.LogDebug(
                "{Worker}: current equity is zero/negative for account {Id} — skipping snapshot (stale or stub reading)",
                WorkerName,
                account.Id);
            return false;
        }

        var latestSnapshot = await db
            .Set<DrawdownSnapshot>()
            .AsNoTracking()
            .OrderByDescending(x => x.RecordedAt)
            .FirstOrDefaultAsync(ct);

        decimal peakEquity = latestSnapshot is not null
            ? Math.Max(latestSnapshot.PeakEquity, currentEquity)
            : currentEquity;

        if (peakEquity <= 0)
        {
            _logger.LogDebug("{Worker}: peak equity is zero, skipping snapshot", WorkerName);
            return false;
        }

        var response = await mediator.Send(new RecordDrawdownSnapshotCommand
        {
            CurrentEquity = currentEquity,
            PeakEquity    = peakEquity
        }, ct);

        if (!response.status)
        {
            _logger.LogWarning(
                "{Worker}: RecordDrawdownSnapshotCommand returned failure (code={Code}, message={Message})",
                WorkerName,
                response.responseCode,
                response.message);
            return false;
        }

        decimal drawdownPct = Math.Max(0m, (peakEquity - currentEquity) / peakEquity * 100m);

        // ── Margin level monitoring ─────────────────────────────────────────
        if (account.MarginUsed > 0)
        {
            decimal marginLevel = account.Equity / account.MarginUsed * 100m;

            // Read configurable margin thresholds from EngineConfig (hot-reloadable).
            decimal criticalMarginPct = await GetDecimalConfigAsync(readContext, CK_CriticalMarginPct, DefaultCriticalMarginPct, ct);
            decimal cautionMarginPct  = await GetDecimalConfigAsync(readContext, CK_CautionMarginPct, DefaultCautionMarginPct, ct);

            if (cautionMarginPct < criticalMarginPct)
            {
                _logger.LogWarning(
                    "{Worker}: margin thresholds misconfigured (caution={Caution:F0}% < critical={Critical:F0}%) — normalising caution to critical",
                    WorkerName,
                    cautionMarginPct,
                    criticalMarginPct);
                cautionMarginPct = criticalMarginPct;
            }

            if (marginLevel < criticalMarginPct)
                _logger.LogError(
                    "{Worker}: critical margin level {Level:F0}% is below {Critical:F0}% (equity={Equity:F2}, margin={Margin:F2}). " +
                    "Broker margin call imminent.",
                    WorkerName, marginLevel, criticalMarginPct, account.Equity, account.MarginUsed);
            else if (marginLevel < cautionMarginPct)
                _logger.LogWarning(
                    "{Worker}: margin level {Level:F0}% is below {Caution:F0}% safety threshold (equity={Equity:F2}, margin={Margin:F2})",
                    WorkerName, marginLevel, cautionMarginPct, account.Equity, account.MarginUsed);
        }

        _logger.LogDebug(
            "{Worker}: snapshot recorded — AccountId={AccountId}, Equity={Equity:F2}, Peak={Peak:F2}, Drawdown={DD:F2}%",
            WorkerName,
            account.Id,
            currentEquity,
            peakEquity,
            drawdownPct);
        return true;
    }

    private async Task<int> GetPollIntervalSecondsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var primaryValue = await GetConfigValueAsync(readContext, CK_PollSecs, ct);
        if (TryParseInt(primaryValue, out var configuredPollSecs) && configuredPollSecs > 0)
            return configuredPollSecs;

        var legacyValue = await GetConfigValueAsync(readContext, CK_PollSecsLegacy, ct);
        if (TryParseInt(legacyValue, out configuredPollSecs) && configuredPollSecs > 0)
        {
            if (!_legacyPollIntervalWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker}: using legacy EngineConfig key '{LegacyKey}'. Rename it to '{CurrentKey}' to avoid future drift.",
                    WorkerName,
                    CK_PollSecsLegacy,
                    CK_PollSecs);
                _legacyPollIntervalWarningEmitted = true;
            }

            return configuredPollSecs;
        }

        return DefaultPollSeconds;
    }

    private async Task<TradingAccount?> GetSingleActiveAccountAsync(
        DbContext db,
        string operationName,
        CancellationToken ct)
    {
        var accounts = await db.Set<TradingAccount>()
            .AsNoTracking()
            .Where(x => x.IsActive && !x.IsDeleted)
            .OrderBy(x => x.Id)
            .Take(2)
            .ToListAsync(ct);

        if (accounts.Count == 0)
        {
            _logger.LogDebug("{Worker}: no active trading account found, skipping {Operation}", WorkerName, operationName);
            return null;
        }

        if (accounts.Count > 1)
        {
            _logger.LogCritical(
                "{Worker}: found multiple active trading accounts ({AccountIds}) while drawdown snapshots are portfolio-global. Skipping {Operation} until account activation state is repaired.",
                WorkerName,
                string.Join(", ", accounts.Select(a => a.Id)),
                operationName);
            return null;
        }

        return accounts[0];
    }

    private static async Task<string?> GetConfigValueAsync(
        IReadApplicationDbContext readContext,
        string key,
        CancellationToken ct)
    {
        var entry = await readContext.GetDbContext()
            .Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        return entry?.Value;
    }

    private static async Task<decimal> GetDecimalConfigAsync(
        IReadApplicationDbContext readContext,
        string key,
        decimal defaultValue,
        CancellationToken ct)
    {
        var value = await GetConfigValueAsync(readContext, key, ct);
        return TryParseDecimal(value, out var parsed) && parsed > 0m
            ? parsed
            : defaultValue;
    }

    private static bool TryParseInt(string? value, out int parsed) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    private static bool TryParseDecimal(string? value, out decimal parsed) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed);
}
