using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically reconciles engine state against broker state reported by EA instances.
/// Runs every hour. For each active EA instance, compares open positions and submitted
/// orders between the engine and the last known broker snapshot, then auto-heals
/// orphaned engine positions (closes them) and logs unknown broker entities.
/// </summary>
public class ReconciliationWorker : BackgroundService
{
    private const string CK_IntervalMins = "Reconciliation:IntervalMinutes";
    private const int DefaultIntervalMinutes = 60;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReconciliationWorker> _logger;
    private readonly TradingMetrics _metrics;
    private int _consecutiveFailures;

    public ReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ReconciliationWorker> logger,
        TradingMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _metrics      = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReconciliationWorker starting");

        // Wait 5 minutes after startup to let EA instances register and send initial snapshots
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            int intervalMins = DefaultIntervalMinutes;
            try
            {
                await ReconcileAsync(stoppingToken);
                _consecutiveFailures = 0;

                using var configScope = _scopeFactory.CreateScope();
                var configCtx = configScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                intervalMins = await GetConfigAsync(configCtx, CK_IntervalMins, DefaultIntervalMinutes, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex,
                    "ReconciliationWorker: error (consecutive failures: {Failures})",
                    _consecutiveFailures);
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "Reconciliation"),
                    new KeyValuePair<string, object?>("reason", "unhandled"));
            }

            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromMinutes(Math.Min(
                    intervalMins * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalMinutes))
                : TimeSpan.FromMinutes(intervalMins);

            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("ReconciliationWorker stopped");
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();
        var readDb       = readContext.GetDbContext();
        var writeDb      = writeContext.GetDbContext();

        // Only reconcile positions that have a broker ticket and belong to active accounts
        var enginePositions = await readDb.Set<Position>()
            .Where(p => p.Status == PositionStatus.Open
                     && p.BrokerPositionId != null
                     && !p.IsDeleted)
            .ToListAsync(ct);

        if (enginePositions.Count == 0)
        {
            _logger.LogDebug("ReconciliationWorker: no open positions to reconcile");
            return;
        }

        // Get all active EA instances and their owned symbols
        var activeInstances = await readDb.Set<EAInstance>()
            .Where(e => e.Status == EAInstanceStatus.Active && !e.IsDeleted)
            .Select(e => new { e.InstanceId, e.Symbols })
            .ToListAsync(ct);

        var symbolsCoveredByEA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in activeInstances)
        {
            foreach (var sym in (instance.Symbols ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                symbolsCoveredByEA.Add(sym);
        }

        // Find engine positions whose symbol has no active EA — these are orphaned
        int orphansClosed = 0;
        foreach (var pos in enginePositions)
        {
            if (symbolsCoveredByEA.Contains(pos.Symbol))
                continue; // EA is alive for this symbol — skip

            try
            {
                // Close the orphaned position — no EA is monitoring it
                var posEntity = await writeDb.Set<Position>()
                    .FirstOrDefaultAsync(p => p.Id == pos.Id && p.Status == PositionStatus.Open, ct);

                if (posEntity is null)
                    continue;

                posEntity.Status = PositionStatus.Closed;
                posEntity.ClosedAt = DateTime.UtcNow;

                await mediator.Send(new LogDecisionCommand
                {
                    EntityType   = "Position",
                    EntityId     = pos.Id,
                    DecisionType = "ReconciliationClosure",
                    Outcome      = "Closed",
                    Reason       = $"No active EA instance covers {pos.Symbol} — orphaned position auto-closed by ReconciliationWorker",
                    Source       = nameof(ReconciliationWorker)
                }, ct);

                orphansClosed++;
                _logger.LogWarning(
                    "ReconciliationWorker: auto-closed orphaned position {Id} ({Symbol}) — no active EA",
                    pos.Id, pos.Symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ReconciliationWorker: failed to close orphaned position {Id} ({Symbol}) — skipping",
                    pos.Id, pos.Symbol);
            }
        }

        // Detect stale submitted orders with no EA coverage
        var staleOrders = await readDb.Set<Order>()
            .Where(o => o.Status == OrderStatus.Submitted && !o.IsDeleted)
            .ToListAsync(ct);

        int ordersExpired = 0;
        foreach (var order in staleOrders)
        {
            if (symbolsCoveredByEA.Contains(order.Symbol))
                continue;

            try
            {
                await writeDb.Set<Order>()
                    .Where(o => o.Id == order.Id && o.Status == OrderStatus.Submitted)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired), ct);

                ordersExpired++;
                _logger.LogWarning(
                    "ReconciliationWorker: expired orphaned order {Id} ({Symbol}) — no active EA",
                    order.Id, order.Symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ReconciliationWorker: failed to expire orphaned order {Id} ({Symbol}) — skipping",
                    order.Id, order.Symbol);
            }
        }

        if (orphansClosed > 0 || ordersExpired > 0)
        {
            await writeContext.SaveChangesAsync(ct);
            _logger.LogInformation(
                "ReconciliationWorker: cycle complete — {PosClosed} positions auto-closed, {OrdExpired} orders expired",
                orphansClosed, ordersExpired);
        }
    }

    private static async Task<int> GetConfigAsync(
        IReadApplicationDbContext readContext, string key, int defaultValue, CancellationToken ct)
    {
        var entry = await readContext.GetDbContext()
            .Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        return int.TryParse(entry.Value, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }
}
