using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ExpireTradeSignal;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

public partial class StrategyWorker
{
    // ════════════════════════════════════════════════════════════════════════════
    //  Signal expiry sweep & stale state cleanup
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Timer callback that triggers the signal expiry sweep. Expired pending/approved signals
    /// are transitioned via CQRS, and stale cooldown/circuit-breaker entries for deactivated
    /// strategies are purged to prevent unbounded dictionary growth.
    ///
    /// Runs on a fire-and-forget <see cref="Task.Run"/> to avoid blocking the timer thread.
    /// Processes up to <see cref="StrategyEvaluatorOptions.ExpirySweepBatchSize"/> signals per cycle.
    /// Protected by a <see cref="SemaphoreSlim"/> to prevent concurrent sweeps.
    /// </summary>
    private void RunExpirySweep(object? state)
    {
        _ = Task.Run(async () =>
        {
            if (_stoppingToken.IsCancellationRequested) return;

            // Skip if a previous sweep is still running.
            // Use _stoppingToken for cancellation so the sweep respects host shutdown.
            if (!await _expirySweepLock.WaitAsync(0, _stoppingToken)) return;

            try
            {
                using var scope  = _scopeFactory.CreateScope();
                var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();
                var context      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

                // Find pending and approved signals that have exceeded their time-to-live
                var expiredIds = await context.GetDbContext()
                    .Set<Domain.Entities.TradeSignal>()
                    .Where(x => (x.Status == TradeSignalStatus.Pending || x.Status == TradeSignalStatus.Approved)
                                && x.ExpiresAt < DateTime.UtcNow
                                && !x.IsDeleted)
                    .OrderBy(x => x.ExpiresAt)
                    .Take(_options.ExpirySweepBatchSize)
                    .Select(x => x.Id)
                    .ToListAsync(_stoppingToken);

                // Expire each signal individually via the CQRS command
                foreach (var id in expiredIds)
                {
                    if (_stoppingToken.IsCancellationRequested) break;
                    await mediator.Send(new ExpireTradeSignalCommand { Id = id }, _stoppingToken);
                }

                if (expiredIds.Count > 0)
                    _logger.LogInformation("Signal expiry sweep: expired {Count} signals", expiredIds.Count);

                // Purge stale cooldown entries for strategies that are no longer active.
                // This prevents the ConcurrentDictionary from growing unbounded over time.
                var activeStrategyIds = await context.GetDbContext()
                    .Set<Domain.Entities.Strategy>()
                    .Where(x => x.Status == StrategyStatus.Active && !x.IsDeleted)
                    .Select(x => x.Id)
                    .ToListAsync(_stoppingToken);

                var activeSet = new HashSet<long>(activeStrategyIds);
                foreach (var key in _lastSignalTime.Keys)
                {
                    if (!activeSet.Contains(key))
                        _lastSignalTime.TryRemove(key, out _);
                }
                foreach (var key in _consecutiveFailures.Keys)
                {
                    if (!activeSet.Contains(key))
                        _consecutiveFailures.TryRemove(key, out _);
                }
                foreach (var key in _circuitOpenedAt.Keys)
                {
                    if (!activeSet.Contains(key))
                        _circuitOpenedAt.TryRemove(key, out _);
                }
            }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — expected
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in signal expiry sweep");
                _metrics.WorkerErrors.Add(1, new("worker", "StrategyWorker"), new("operation", "expiry_sweep"));
            }
            finally
            {
                _expirySweepLock.Release();
            }
        }, CancellationToken.None);
    }
}
