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
    private readonly ILogger<DrawdownMonitorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;

    private const string CK_PollSecs            = "Drawdown:PollIntervalSeconds";
    private const string CK_EmergencyLossPct    = "Drawdown:EmergencyLossPct";
    private const string CK_CriticalMarginPct   = "Drawdown:CriticalMarginPct";
    private const string CK_CautionMarginPct    = "Drawdown:CautionMarginPct";
    private const int DefaultPollSeconds        = 60;
    private const decimal DefaultEmergencyPct   = 2.0m; // 2% of equity triggers emergency snapshot

    /// <summary>Max backoff delay on consecutive failures (5 minutes).</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>Cooldown to prevent flooding from rapid position closures.</summary>
    private DateTime _lastEmergencySnapshot = DateTime.MinValue;
    private static readonly TimeSpan EmergencyCooldown = TimeSpan.FromSeconds(10);

    private int _consecutiveFailures;

    public DrawdownMonitorWorker(
        ILogger<DrawdownMonitorWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _metrics      = metrics;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Polling mode — regular scheduled snapshots
    // ═══════════════════════════════════════════════════════════════════════

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DrawdownMonitorWorker starting (hybrid: polling + event-driven)");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSeconds;
            try
            {
                await RecordSnapshotAsync(stoppingToken);
                _consecutiveFailures = 0;

                // Read configurable poll interval from EngineConfig
                using var configScope = _scopeFactory.CreateScope();
                var configCtx = configScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var entry = await configCtx.GetDbContext()
                    .Set<Domain.Entities.EngineConfig>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Key == CK_PollSecs, stoppingToken);
                if (entry?.Value is not null && int.TryParse(entry.Value, out var parsed) && parsed > 0)
                    pollSecs = parsed;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex,
                    "DrawdownMonitorWorker: polling error (consecutive failures: {Failures})",
                    _consecutiveFailures);
            }

            var pollingInterval = TimeSpan.FromSeconds(pollSecs);

            // Exponential backoff on consecutive failures: 60s, 120s, 240s, capped at 5min
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    pollingInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : pollingInterval;

            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("DrawdownMonitorWorker stopped");
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

        // Cooldown: don't flood snapshots from rapid consecutive closures
        if ((DateTime.UtcNow - _lastEmergencySnapshot) < EmergencyCooldown)
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

            // Load the active account to compute loss as % of equity
            var account = await readContext.GetDbContext()
                .Set<TradingAccount>()
                .Where(x => x.IsActive && !x.IsDeleted)
                .FirstOrDefaultAsync();

            if (account is null || account.Equity <= 0)
                return;

            // Read configurable emergency threshold from EngineConfig
            decimal emergencyPct = DefaultEmergencyPct;
            var configEntry = await readContext.GetDbContext()
                .Set<Domain.Entities.EngineConfig>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == CK_EmergencyLossPct);
            if (configEntry?.Value is not null && decimal.TryParse(configEntry.Value, out var cfgParsed) && cfgParsed > 0)
                emergencyPct = cfgParsed;

            decimal lossPct = Math.Abs(@event.RealisedPnL) / account.Equity * 100m;
            if (lossPct < emergencyPct)
                return; // Loss is small relative to equity — normal polling will catch it

            _lastEmergencySnapshot = DateTime.UtcNow;

            _logger.LogWarning(
                "DrawdownMonitorWorker: EMERGENCY snapshot triggered — position {PositionId} ({Symbol}) " +
                "closed with {LossPct:F2}% loss ({PnL:F2}) exceeding {Threshold:F1}% threshold",
                @event.PositionId, @event.Symbol, lossPct, @event.RealisedPnL, emergencyPct);

            _metrics.WorkerErrors.Add(1,
                new KeyValuePair<string, object?>("worker", "DrawdownMonitor"),
                new KeyValuePair<string, object?>("reason", "emergency_snapshot"));

            await RecordSnapshotAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "DrawdownMonitorWorker: failed to record emergency snapshot after position {PositionId} closure",
                @event.PositionId);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Shared snapshot logic
    // ═══════════════════════════════════════════════════════════════════════

    private async Task RecordSnapshotAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator      = scope.ServiceProvider.GetRequiredService<IMediator>();

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

        // Zero/negative equity is virtually always a stale or stub account reading —
        // real drawdowns approach zero but don't hit exactly 0.00000000. Recording a
        // snapshot with a zero currentEquity against a positive historical peakEquity
        // synthesises DrawdownPct=100.00%, which trips the Halted threshold and causes
        // DrawdownRecoveryWorker to pause every active strategy on bogus data. Skip
        // the snapshot entirely until a real equity value arrives.
        if (currentEquity <= 0)
        {
            _logger.LogDebug(
                "DrawdownMonitorWorker: current equity is zero/negative for account {Id} — skipping snapshot (stale or stub reading)",
                account.Id);
            return;
        }

        var latestSnapshot = await readContext.GetDbContext()
            .Set<DrawdownSnapshot>()
            .OrderByDescending(x => x.RecordedAt)
            .FirstOrDefaultAsync(ct);

        decimal peakEquity = latestSnapshot is not null
            ? Math.Max(latestSnapshot.PeakEquity, currentEquity)
            : currentEquity;

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

        // ── Margin level monitoring ─────────────────────────────────────────
        if (account.MarginUsed > 0)
        {
            decimal marginLevel = account.Equity / account.MarginUsed * 100m;

            // Read configurable margin thresholds from EngineConfig (hot-reloadable).
            decimal criticalMarginPct = await GetConfigAsync(readContext, CK_CriticalMarginPct, 150.0m, ct);
            decimal cautionMarginPct  = await GetConfigAsync(readContext, CK_CautionMarginPct, 200.0m, ct);

            if (marginLevel < criticalMarginPct)
                _logger.LogError(
                    "DrawdownMonitorWorker: CRITICAL — margin level {Level:F0}% is below {Critical:F0}% (equity={Equity:F2}, margin={Margin:F2}). " +
                    "Broker margin call imminent.",
                    marginLevel, criticalMarginPct, account.Equity, account.MarginUsed);
            else if (marginLevel < cautionMarginPct)
                _logger.LogWarning(
                    "DrawdownMonitorWorker: margin level {Level:F0}% is below {Caution:F0}% safety threshold (equity={Equity:F2}, margin={Margin:F2})",
                    marginLevel, cautionMarginPct, account.Equity, account.MarginUsed);
        }

        _logger.LogInformation(
            "DrawdownMonitorWorker: snapshot recorded — Equity={Equity:F2}, Peak={Peak:F2}, Drawdown={DD:F2}%",
            currentEquity, peakEquity, drawdownPct);
    }

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/> by key, falling back to
    /// <paramref name="defaultValue"/> if the key is missing or the stored value
    /// cannot be converted.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        IReadApplicationDbContext readContext, string key, T defaultValue, CancellationToken ct)
    {
        var entry = await readContext.GetDbContext()
            .Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
