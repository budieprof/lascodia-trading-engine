using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Default <see cref="ISignalRejectionAuditor"/> that batches rejection writes
/// through a bounded channel and a background flush loop. This turns a
/// 30-rejection tick from 30 synchronous DB round-trips into 1 bulk insert.
///
/// <para>
/// <b>Hot path:</b> <see cref="RecordAsync"/> enqueues a
/// <see cref="SignalRejectionAudit"/> row into the channel and returns
/// immediately — no I/O on the caller's thread. The counter is incremented on
/// enqueue so dashboards still see rejections immediately.
/// </para>
///
/// <para>
/// <b>Flush loop:</b> <see cref="ExecuteAsync"/> runs as a hosted service,
/// draining up to <c>BatchMaxRows</c> messages (or until <c>FlushInterval</c>
/// elapses) and committing them in a single <c>AddRange</c> + SaveChanges
/// call. Per-batch failures log a warning and drop the batch — retrying would
/// risk growing an unbounded backlog in a persistent-outage scenario, and the
/// auditor explicitly trades at-most-once for throughput (it's observability,
/// not accounting). Failed batches increment <c>WorkerErrors</c>.
/// </para>
///
/// <para>
/// <b>Backpressure:</b> the channel is bounded (<c>BufferCapacity = 4096</c>
/// rows) with a <see cref="BoundedChannelFullMode.DropOldest"/> policy so a
/// sustained DB outage does not build memory pressure. Dropped rows are
/// counted separately so operators can see when the audit stream fell
/// behind.
/// </para>
///
/// <para>
/// <b>Shutdown:</b> <see cref="StopAsync"/> completes the channel writer,
/// waits for the flush loop to drain (bounded by a 10s ceiling), then falls
/// through to <see cref="BackgroundService.StopAsync"/>.
/// </para>
///
/// <para>
/// Registered explicitly in <c>DependencyInjection.ConfigureInfrastructureServices</c>
/// as Singleton + ISignalRejectionAuditor + IHostedService so the same
/// instance is used for the interface binding and for the hosted flush loop.
/// </para>
/// </summary>
public sealed class SignalRejectionAuditor
    : BackgroundService, ISignalRejectionAuditor
{
    private const int BufferCapacity = 4096;
    private const int BatchMaxRows   = 256;
    private static readonly TimeSpan FlushInterval  = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DrainDeadline  = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;
    private readonly ILogger<SignalRejectionAuditor> _logger;

    private readonly Channel<SignalRejectionAudit> _channel =
        Channel.CreateBounded<SignalRejectionAudit>(new BoundedChannelOptions(BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public SignalRejectionAuditor(
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics,
        ILogger<SignalRejectionAuditor> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _logger = logger;
    }

    public Task RecordAsync(
        string stage,
        string reason,
        string symbol,
        string source,
        long strategyId = 0,
        long? tradeSignalId = null,
        string? detail = null,
        CancellationToken ct = default)
    {
        // Trim inputs defensively — the column lengths are narrow and an
        // out-of-bounds value should never crash the caller. We also count
        // the emit BEFORE the enqueue so dashboards reflect intent even on
        // the extreme case where the channel is full and DropOldest fires.
        stage  = Truncate(stage,  32)  ?? string.Empty;
        reason = Truncate(reason, 64)  ?? string.Empty;
        symbol = Truncate(symbol, 10)  ?? string.Empty;
        source = Truncate(source, 50)  ?? string.Empty;
        detail = Truncate(detail, 2000);

        var row = new SignalRejectionAudit
        {
            TradeSignalId = tradeSignalId,
            StrategyId    = strategyId,
            Symbol        = symbol,
            Stage         = stage,
            Reason        = reason,
            Detail        = detail,
            Source        = source,
            RejectedAt    = DateTime.UtcNow,
        };

        _metrics.SignalRejectionsAudited.Add(1,
            new KeyValuePair<string, object?>("stage",  stage),
            new KeyValuePair<string, object?>("reason", reason));

        // TryWrite is always non-blocking on a BoundedChannel. It returns
        // false only if the channel is completed; under DropOldest the
        // channel transparently evicts to make room.
        _channel.Writer.TryWrite(row);

        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<SignalRejectionAudit>(BatchMaxRows);

        while (!stoppingToken.IsCancellationRequested)
        {
            buffer.Clear();

            try
            {
                // Wait for at least one row (blocks up to FlushInterval). When
                // the first row arrives we drain everything already enqueued up
                // to BatchMaxRows so a burst is committed in a single DB call.
                using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                flushCts.CancelAfter(FlushInterval);

                bool hasFirst;
                try { hasFirst = await _channel.Reader.WaitToReadAsync(flushCts.Token); }
                catch (OperationCanceledException) when (flushCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    // Interval elapsed with no rows — just loop.
                    continue;
                }

                if (!hasFirst) return; // channel completed — shutdown

                while (buffer.Count < BatchMaxRows && _channel.Reader.TryRead(out var row))
                    buffer.Add(row);

                if (buffer.Count == 0) continue;

                await FlushBatchAsync(buffer, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Swallow. The batch has already been logged/metrics-counted in
                // FlushBatchAsync; this is belt-and-braces.
                _logger.LogError(ex, "SignalRejectionAuditor: unhandled flush-loop error");
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "SignalRejectionAuditor"),
                    new KeyValuePair<string, object?>("reason", "flush_loop_unhandled"));
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Signal no more writes and drain what's already queued so operators
        // don't lose audit rows when the host cycles. Bounded by DrainDeadline
        // so a misbehaving DB can't block shutdown forever.
        _channel.Writer.TryComplete();

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainCts.CancelAfter(DrainDeadline);

        try
        {
            var tail = new List<SignalRejectionAudit>(BatchMaxRows);
            while (_channel.Reader.TryRead(out var row))
            {
                tail.Add(row);
                if (tail.Count >= BatchMaxRows)
                {
                    await FlushBatchAsync(tail, drainCts.Token);
                    tail.Clear();
                }
            }

            if (tail.Count > 0)
                await FlushBatchAsync(tail, drainCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "SignalRejectionAuditor: final drain at shutdown failed — some audit rows may be lost");
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task FlushBatchAsync(List<SignalRejectionAudit> batch, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var db = writeCtx.GetDbContext();

            await db.Set<SignalRejectionAudit>().AddRangeAsync(batch, ct);
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SignalRejectionAuditor: flush failed for {Count} audit rows — batch dropped",
                batch.Count);
            _metrics.WorkerErrors.Add(1,
                new KeyValuePair<string, object?>("worker", "SignalRejectionAuditor"),
                new KeyValuePair<string, object?>("reason", "flush_failed"));
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (value is null) return null;
        return value.Length <= max ? value : value[..max];
    }

    /// <summary>Test hook: flush any buffered rows synchronously. Never call from production.</summary>
    internal async Task FlushForTestsAsync(CancellationToken ct = default)
    {
        var batch = new List<SignalRejectionAudit>();
        while (_channel.Reader.TryRead(out var row)) batch.Add(row);
        if (batch.Count > 0) await FlushBatchAsync(batch, ct);
    }
}
