using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects orders stuck in <see cref="OrderStatus.Submitted"/> status without an execution
/// report from the EA, and queues <see cref="EACommandType.RequestExecutionStatus"/> commands
/// so the EA can report the fill/rejection status of those orders.
///
/// <para>
/// <b>Why this matters:</b> If the EA executes an order on MT5 but the HTTP call to
/// <c>POST /order/{id}/execution-report</c> fails (network blip, EA restart, timeout),
/// the engine has no record of the fill. The order stays in <c>Submitted</c> indefinitely,
/// and no position is created. This worker closes that gap by proactively requesting
/// the execution status from the EA.
/// </para>
///
/// <para>
/// <b>Flow:</b>
/// <list type="number">
///   <item>Find orders in <c>Submitted</c> status older than <c>StaleOrder:ThresholdMinutes</c>
///         (default 5 minutes).</item>
///   <item>For each stale order, check if a <c>RequestExecutionStatus</c> command was already
///         queued (idempotency — avoid flooding the EA with duplicate requests).</item>
///   <item>Queue a new <see cref="EACommand"/> with <see cref="EACommandType.RequestExecutionStatus"/>
///         targeting the EA instance that owns the order's symbol.</item>
///   <item>If no EA instance owns the symbol, expire the order after
///         <c>StaleOrder:ExpireAfterMinutes</c> (default 30 minutes).</item>
/// </list>
/// </para>
/// </summary>
public sealed class StaleOrderRecoveryWorker : BackgroundService
{
    private const string CK_PollSecs      = "StaleOrder:PollIntervalSeconds";
    private const string CK_ThresholdMins = "StaleOrder:ThresholdMinutes";
    private const string CK_ExpireMins    = "StaleOrder:ExpireAfterMinutes";
    private const int DefaultPollSeconds     = 60;
    private const int DefaultThresholdMins   = 5;
    private const int DefaultExpireAfterMins = 30;

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StaleOrderRecoveryWorker> _logger;
    private int _consecutiveFailures;

    public StaleOrderRecoveryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<StaleOrderRecoveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StaleOrderRecoveryWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSeconds;
            try
            {
                await RecoverStaleOrdersAsync(stoppingToken);
                _consecutiveFailures = 0;

                using var configScope = _scopeFactory.CreateScope();
                var configCtx = configScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                pollSecs = await GetConfigAsync<int>(configCtx, CK_PollSecs, DefaultPollSeconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex,
                    "StaleOrderRecoveryWorker: error (consecutive failures: {Failures})",
                    _consecutiveFailures);
            }

            var pollingInterval = TimeSpan.FromSeconds(pollSecs);
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    pollingInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : pollingInterval;

            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("StaleOrderRecoveryWorker stopped");
    }

    private async Task RecoverStaleOrdersAsync(CancellationToken ct)
    {
        using var scope      = _scopeFactory.CreateScope();
        var readContext      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeContext     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();

        var readDb  = readContext.GetDbContext();
        var writeDb = writeContext.GetDbContext();

        int thresholdMins = await GetConfigAsync<int>(readContext, CK_ThresholdMins, DefaultThresholdMins, ct);
        int expireMins    = await GetConfigAsync<int>(readContext, CK_ExpireMins, DefaultExpireAfterMins, ct);

        var staleCutoff  = DateTime.UtcNow.AddMinutes(-thresholdMins);
        var expireCutoff = DateTime.UtcNow.AddMinutes(-expireMins);

        // Find orders stuck in Submitted status beyond the threshold
        var staleOrders = await readDb.Set<Order>()
            .Where(o => o.Status == OrderStatus.Submitted
                     && o.CreatedAt < staleCutoff
                     && !o.IsDeleted)
            .OrderBy(o => o.CreatedAt)
            .Take(50) // Process up to 50 per cycle
            .Select(o => new { o.Id, o.Symbol, o.BrokerOrderId, o.CreatedAt })
            .ToListAsync(ct);

        if (staleOrders.Count == 0) return;

        // Check which orders already have a pending RequestExecutionStatus command
        var staleOrderIds = staleOrders.Select(o => o.Id).ToList();
        var existingCommands = await readDb.Set<EACommand>()
            .Where(c => c.CommandType == EACommandType.RequestExecutionStatus
                     && !c.Acknowledged
                     && !c.IsDeleted)
            .Select(c => c.Parameters)
            .ToListAsync(ct);

        // Parse existing command parameters to extract order IDs for dedup.
        // Supports both versioned (v >= 1) and legacy (no version field) formats.
        var alreadyRequestedOrderIds = new HashSet<long>();
        foreach (var param in existingCommands)
        {
            if (param is null) continue;
            try
            {
                using var doc = JsonDocument.Parse(param);
                if (doc.RootElement.TryGetProperty("engineOrderId", out var prop) && prop.TryGetInt64(out var id))
                    alreadyRequestedOrderIds.Add(id);
            }
            catch { /* ignore malformed parameters */ }
        }

        int queued = 0, expired = 0;

        foreach (var order in staleOrders)
        {
            // Skip if we already have a pending command for this order
            if (alreadyRequestedOrderIds.Contains(order.Id))
                continue;

            // Find the EA instance that owns this symbol
            var eaInstance = await readDb.Set<EAInstance>()
                .ActiveForSymbol(order.Symbol)
                .FirstOrDefaultAsync(ct);

            if (eaInstance is not null)
            {
                // Queue a RequestExecutionStatus command for the EA
                await writeDb.Set<EACommand>().AddAsync(new EACommand
                {
                    TargetInstanceId = eaInstance.InstanceId,
                    CommandType      = EACommandType.RequestExecutionStatus,
                    TargetTicket     = !string.IsNullOrEmpty(order.BrokerOrderId) && long.TryParse(order.BrokerOrderId, out var ticket) ? ticket : null,
                    Symbol           = order.Symbol,
                    // Version field ensures forward-compatible deserialization if the schema evolves.
                    Parameters       = JsonSerializer.Serialize(new { version = 1, engineOrderId = order.Id, brokerOrderId = order.BrokerOrderId }),
                }, ct);
                queued++;

                _logger.LogWarning(
                    "StaleOrderRecoveryWorker: order {OrderId} ({Symbol}) stuck in Submitted for {Age:F0} min — queued RequestExecutionStatus to EA {InstanceId}",
                    order.Id, order.Symbol, (DateTime.UtcNow - order.CreatedAt).TotalMinutes, eaInstance.InstanceId);
            }
            else if (order.CreatedAt < expireCutoff)
            {
                // No EA available and order is very old — expire it
                await writeDb.Set<Order>()
                    .Where(o => o.Id == order.Id && o.Status == OrderStatus.Submitted)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired), ct);
                expired++;

                _logger.LogWarning(
                    "StaleOrderRecoveryWorker: order {OrderId} ({Symbol}) expired — no EA instance available and order is {Age:F0} min old (threshold: {Threshold} min)",
                    order.Id, order.Symbol, (DateTime.UtcNow - order.CreatedAt).TotalMinutes, expireMins);
            }
        }

        if (queued > 0 || expired > 0)
        {
            await writeContext.SaveChangesAsync(ct);
            _logger.LogInformation(
                "StaleOrderRecoveryWorker: processed {Total} stale orders — {Queued} recovery commands queued, {Expired} expired",
                staleOrders.Count, queued, expired);
        }
    }

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
