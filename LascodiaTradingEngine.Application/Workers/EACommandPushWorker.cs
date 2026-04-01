using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Polls for un-acknowledged EA commands and pushes them to connected EA instances via WebSocket.
/// Reduces the latency between command creation and EA execution from the EA's poll interval
/// (~1-2s) to near-zero. Falls back gracefully to poll-based delivery when WebSocket is
/// unavailable — the EA's existing GET /ea/commands endpoint remains the source of truth.
/// Only active when <see cref="WebSocketBridgeOptions.Enabled"/> is <c>true</c>.
/// </summary>
public class EACommandPushWorker : BackgroundService
{
    private readonly ILogger<EACommandPushWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWebSocketBridge _wsBridge;
    private readonly WebSocketBridgeOptions _wsOptions;
    private const int PollIntervalMs = 500;
    private const int MaxBackoffMs = 30_000;
    private int _consecutiveFailures;

    /// <summary>
    /// Tracks command IDs that have already been pushed to avoid redundant pushes
    /// on subsequent poll cycles before the EA acknowledges them.
    /// </summary>
    private readonly HashSet<long> _pushedCommandIds = new();

    public EACommandPushWorker(
        ILogger<EACommandPushWorker> logger,
        IServiceScopeFactory scopeFactory,
        IWebSocketBridge wsBridge,
        WebSocketBridgeOptions wsOptions)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _wsBridge     = wsBridge;
        _wsOptions    = wsOptions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_wsOptions.Enabled)
        {
            _logger.LogInformation("EACommandPushWorker: WebSocket bridge disabled — exiting");
            return;
        }

        _logger.LogInformation("EACommandPushWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PushPendingCommandsAsync(stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "EACommandPushWorker error (failure #{Count})", _consecutiveFailures);
            }

            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromMilliseconds(Math.Min(PollIntervalMs * Math.Pow(2, _consecutiveFailures - 1), MaxBackoffMs))
                : TimeSpan.FromMilliseconds(PollIntervalMs);
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task PushPendingCommandsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        // Fetch un-acknowledged commands that haven't been pushed yet
        var pendingCommands = await readCtx.GetDbContext()
            .Set<EACommand>()
            .Where(c => !c.Acknowledged && !c.IsDeleted)
            .OrderBy(c => c.CreatedAt)
            .Take(50) // batch limit per cycle
            .ToListAsync(ct);

        if (pendingCommands.Count == 0)
        {
            // Periodically clear the pushed set when no commands are pending
            _pushedCommandIds.Clear();
            return;
        }

        int pushed = 0;
        foreach (var command in pendingCommands)
        {
            if (ct.IsCancellationRequested) break;

            // Skip if already pushed in a previous cycle (still awaiting ack)
            if (!_pushedCommandIds.Add(command.Id))
                continue;

            // Only push if the target EA instance has an active WebSocket connection
            if (!_wsBridge.IsConnected(command.TargetInstanceId))
                continue;

            var success = await _wsBridge.PushCommandAsync(command.TargetInstanceId, command, ct);
            if (success)
            {
                pushed++;
            }
            else
            {
                // Remove from pushed set so we retry on next cycle
                _pushedCommandIds.Remove(command.Id);
            }
        }

        // Evict stale entries: remove IDs that are no longer in the pending set
        var pendingIds = new HashSet<long>(pendingCommands.Select(c => c.Id));
        _pushedCommandIds.IntersectWith(pendingIds);

        if (pushed > 0)
            _logger.LogDebug("EACommandPushWorker: pushed {Count} commands via WebSocket", pushed);
    }
}
