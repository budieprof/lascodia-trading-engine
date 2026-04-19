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
            // The sweep body respects _stoppingToken for per-item cancellation so
            // the timer-driven path exits cleanly on shutdown.
            await PerformExpirySweepAsync(_stoppingToken);
        }, CancellationToken.None);
    }

    /// <summary>
    /// Core expiry-sweep body. Extracted so a graceful shutdown can invoke one final
    /// sweep synchronously (via <see cref="StopAsync"/>) to drain in-flight expirations
    /// that would otherwise be dropped when the timer is disposed.
    /// </summary>
    private async Task PerformExpirySweepAsync(CancellationToken ct)
    {
        // Skip if a previous sweep is still running (timer-driven path).
        if (!await _expirySweepLock.WaitAsync(0, ct)) return;

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
                .ToListAsync(ct);

            // Expire each signal individually via the CQRS command
            foreach (var id in expiredIds)
            {
                if (ct.IsCancellationRequested) break;
                await mediator.Send(new ExpireTradeSignalCommand { Id = id }, ct);
            }

            if (expiredIds.Count > 0)
                _logger.LogInformation("Signal expiry sweep: expired {Count} signals", expiredIds.Count);

            // Purge stale cooldown entries for strategies that are no longer active.
            // This prevents the ConcurrentDictionary from growing unbounded over time.
            var activeStrategyIds = await context.GetDbContext()
                .Set<Domain.Entities.Strategy>()
                .Where(x => x.Status == StrategyStatus.Active && !x.IsDeleted)
                .Select(x => x.Id)
                .ToListAsync(ct);

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

            // ── Persist runtime state back to DB so cooldown / circuit-breaker
            //    state survives the next process restart. Bulk UPDATE via
            //    ExecuteUpdateAsync keeps this cheap (one round-trip per
            //    differing state, no change-tracking overhead).
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var writeDb  = writeCtx.GetDbContext();
            int persistErrors = 0;
            foreach (var id in activeStrategyIds)
            {
                if (ct.IsCancellationRequested) break;
                _lastSignalTime.TryGetValue(id, out var lastSignalAt);
                _consecutiveFailures.TryGetValue(id, out var consecFailures);
                _circuitOpenedAt.TryGetValue(id, out var circuitOpenedAt);
                DateTime? lastSignalAtNullable     = lastSignalAt == default ? null : lastSignalAt;
                DateTime? circuitOpenedAtNullable  = circuitOpenedAt == default ? null : circuitOpenedAt;
                try
                {
                    await writeDb.Set<Domain.Entities.Strategy>()
                        .Where(s => s.Id == id)
                        .ExecuteUpdateAsync(u => u
                            .SetProperty(s => s.LastSignalAt, lastSignalAtNullable)
                            .SetProperty(s => s.ConsecutiveEvaluationFailures, consecFailures)
                            .SetProperty(s => s.CircuitOpenedAt, circuitOpenedAtNullable),
                            ct);
                }
                catch (Exception pex)
                {
                    persistErrors++;
                    if (persistErrors <= 3) // cap log spam
                        _logger.LogWarning(pex, "StrategyWorker: state write-back failed for strategy {Id}", id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
    }

    /// <summary>
    /// Override the hosted-service stop to flush one final expiry sweep before the host
    /// tears down. Without this, a signal that crossed its ExpiresAt between the last
    /// timer tick and shutdown would stay in Pending/Approved state across the restart
    /// and the EA might act on stale data. Bounded to 10s so a misbehaving sweep can't
    /// block the shutdown indefinitely.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _expirySweepTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _stateFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            flushCts.CancelAfter(TimeSpan.FromSeconds(10));
            // Persist dirty runtime state before shutdown so restart losses are
            // bounded by seconds, not the fast-flush cadence.
            await FlushDirtyStrategyStateAsync(flushCts.Token);
            await PerformExpirySweepAsync(flushCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "StrategyWorker: final flush/sweep at shutdown failed");
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Timer callback: periodically flush dirty runtime state (lastSignalAt /
    /// consecutiveFailures / circuitOpenedAt) to the <c>Strategy</c> table so
    /// cooldown / circuit state survives restarts with sub-minute staleness.
    /// Companion to the 5-min expiry sweep; the sweep remains as a safety net for
    /// any state change the flusher missed (e.g. dropped dirty flag under error).
    /// </summary>
    private void RunStateFlush(object? state)
    {
        _ = Task.Run(async () =>
        {
            if (_stoppingToken.IsCancellationRequested) return;
            await FlushDirtyStrategyStateAsync(_stoppingToken);
        }, CancellationToken.None);
    }

    /// <summary>
    /// Core flush body. Snaps the current set of dirty strategy IDs, clears them,
    /// and writes each one's in-memory state (<see cref="_lastSignalTime"/> /
    /// <see cref="_consecutiveFailures"/> / <see cref="_circuitOpenedAt"/>) to the
    /// DB via a bulk ExecuteUpdate. Per-strategy errors log but don't abort the
    /// whole flush — a corrupt row shouldn't block the rest of the fleet.
    /// </summary>
    private async Task FlushDirtyStrategyStateAsync(CancellationToken ct)
    {
        if (!await _stateFlushLock.WaitAsync(0, ct)) return;
        try
        {
            // Snapshot-and-clear: drain the current dirty set. Any writes that
            // happen while we're persisting will flag the strategy dirty again
            // for the next tick, so we never drop a real change.
            var dirtyIds = _dirtyStrategyIds.Keys.ToArray();
            foreach (var id in dirtyIds) _dirtyStrategyIds.TryRemove(id, out _);
            if (dirtyIds.Length == 0) return;

            using var scope = _scopeFactory.CreateScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var writeDb  = writeCtx.GetDbContext();

            int errors = 0;
            foreach (var id in dirtyIds)
            {
                if (ct.IsCancellationRequested) break;
                _lastSignalTime.TryGetValue(id, out var lastSignalAt);
                _consecutiveFailures.TryGetValue(id, out var consecFailures);
                _circuitOpenedAt.TryGetValue(id, out var circuitOpenedAt);
                DateTime? lastSignalAtNullable    = lastSignalAt == default ? null : lastSignalAt;
                DateTime? circuitOpenedAtNullable = circuitOpenedAt == default ? null : circuitOpenedAt;

                try
                {
                    await writeDb.Set<Domain.Entities.Strategy>()
                        .Where(s => s.Id == id)
                        .ExecuteUpdateAsync(u => u
                            .SetProperty(s => s.LastSignalAt, lastSignalAtNullable)
                            .SetProperty(s => s.ConsecutiveEvaluationFailures, consecFailures)
                            .SetProperty(s => s.CircuitOpenedAt, circuitOpenedAtNullable),
                            ct);
                }
                catch (Exception ex)
                {
                    errors++;
                    if (errors <= 3)
                        _logger.LogWarning(ex, "StrategyWorker: state flush failed for strategy {Id}", id);
                    // Put the ID back in the dirty set so the next tick retries it.
                    _dirtyStrategyIds[id] = 1;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StrategyWorker: runtime-state flush failed");
        }
        finally
        {
            _stateFlushLock.Release();
        }
    }
}
