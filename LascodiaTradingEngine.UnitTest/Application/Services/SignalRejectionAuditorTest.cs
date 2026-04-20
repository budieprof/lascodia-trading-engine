using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

/// <summary>
/// Exercises the batched <see cref="SignalRejectionAuditor"/>:
/// <list type="bullet">
///   <item>RecordAsync is non-blocking — rows land in the channel, not the DB.</item>
///   <item>The SignalRejectionsAudited metric increments at enqueue, not at flush.</item>
///   <item>A test-only <c>FlushForTestsAsync</c> drains the channel synchronously
///         so tests can assert on the DB insert without depending on the
///         BackgroundService flush loop.</item>
/// </list>
/// </summary>
public class SignalRejectionAuditorTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly Mock<IWriteApplicationDbContext> _writeCtx = new();
    private readonly Mock<DbContext> _db = new();
    private readonly Mock<DbSet<SignalRejectionAudit>> _set = new();
    private readonly List<SignalRejectionAudit> _captured = new();
    private readonly SignalRejectionAuditor _auditor;

    public SignalRejectionAuditorTest()
    {
        _metrics = new TradingMetrics(_meterFactory);

        _writeCtx.Setup(w => w.GetDbContext()).Returns(_db.Object);
        _writeCtx.Setup(w => w.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        // Capture AddRangeAsync into a list so tests can assert shape/truncation.
        _set.Setup(s => s.AddRangeAsync(It.IsAny<IEnumerable<SignalRejectionAudit>>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<SignalRejectionAudit>, CancellationToken>((rows, _) =>
            {
                _captured.AddRange(rows);
                return Task.CompletedTask;
            });
        _db.Setup(d => d.Set<SignalRejectionAudit>()).Returns(_set.Object);

        var scope = new Mock<IServiceScope>();
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_writeCtx.Object);
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        _scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        _auditor = new SignalRejectionAuditor(_scopeFactory.Object, _metrics, Mock.Of<ILogger<SignalRejectionAuditor>>());
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task RecordAsync_IsNonBlocking_AndCountsMetricOnEnqueue()
    {
        using var counter = new CounterProbe(_meterFactory, "trading.signals.rejections_audited");

        await _auditor.RecordAsync(
            stage: "MTF", reason: "mtf_not_confirmed",
            symbol: "EURUSD", source: "StrategyWorker",
            strategyId: 42, tradeSignalId: 99);

        // Metric incremented on enqueue (immediate visibility on dashboards).
        Assert.Equal(1L, counter.Total);
        Assert.Contains("MTF", counter.TagValues("stage"));
        Assert.Contains("mtf_not_confirmed", counter.TagValues("reason"));

        // Nothing should have hit the DB yet — that happens on flush.
        _set.Verify(s => s.AddRangeAsync(It.IsAny<IEnumerable<SignalRejectionAudit>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FlushForTestsAsync_PersistsBatchedRows()
    {
        await _auditor.RecordAsync("Regime", "regime_blocked", "EURUSD", "StrategyWorker", strategyId: 1);
        await _auditor.RecordAsync("MTF",    "mtf_not_confirmed", "EURUSD", "StrategyWorker", strategyId: 1);

        await _auditor.FlushForTestsAsync();

        Assert.Equal(2, _captured.Count);
        _writeCtx.Verify(w => w.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordAsync_TruncatesOverlongFields_BeforeEnqueue()
    {
        var longStage  = new string('s', 100);
        var longReason = new string('r', 200);
        var longDetail = new string('d', 5000);

        await _auditor.RecordAsync(
            stage: longStage, reason: longReason,
            symbol: "EURUSD", source: "worker",
            detail: longDetail);

        await _auditor.FlushForTestsAsync();

        var row = Assert.Single(_captured);
        Assert.Equal(32, row.Stage.Length);
        Assert.Equal(64, row.Reason.Length);
        Assert.Equal(2000, row.Detail!.Length);
    }

    [Fact]
    public async Task FlushFailure_DoesNotEscapeToCaller_AndIncrementsWorkerError()
    {
        _writeCtx.Setup(w => w.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated DB crash"));

        using var workerErrCounter = new CounterProbe(_meterFactory, "trading.workers.errors");

        await _auditor.RecordAsync("Tier1", "some_reason", "EURUSD", "SignalOrderBridgeWorker");
        await _auditor.FlushForTestsAsync(); // would throw if the error leaked

        Assert.True(workerErrCounter.Total >= 1, "FlushBatchAsync should increment WorkerErrors on DB failure.");
    }

    [Fact]
    public async Task RecordAsync_CallerCancellationDoesNotThrow_BatchedWriterIsNonBlocking()
    {
        // In the batched design RecordAsync doesn't await DB, so a pre-cancelled
        // token from the caller has nothing to cancel — the enqueue still
        // succeeds. This is intentional: callers must never have their
        // rejection decision contaminated by audit-plumbing failures.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => _auditor.RecordAsync(
            "Test", "test_reason", "EURUSD", "Test", ct: cts.Token));

        Assert.Null(ex);
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }

    private sealed class CounterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;
        private readonly List<KeyValuePair<string, object?>[]> _tagSnapshots = new();
        private readonly object _lock = new();

        public CounterProbe(IMeterFactory factory, string instrumentName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == instrumentName) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                lock (_lock)
                {
                    _total += value;
                    _tagSnapshots.Add(tags.ToArray());
                }
            });
            _listener.Start();
        }

        public long Total { get { lock (_lock) return _total; } }

        public IEnumerable<string> TagValues(string key)
        {
            lock (_lock)
            {
                return _tagSnapshots.SelectMany(s => s)
                    .Where(kv => kv.Key == key)
                    .Select(kv => kv.Value?.ToString() ?? string.Empty)
                    .ToList();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
