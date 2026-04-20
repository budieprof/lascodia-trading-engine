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

public class SignalRejectionAuditorTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly Mock<IWriteApplicationDbContext> _writeCtx = new();
    private readonly Mock<DbContext> _db = new();
    private readonly SignalRejectionAuditor _auditor;

    public SignalRejectionAuditorTest()
    {
        _metrics = new TradingMetrics(_meterFactory);

        _writeCtx.Setup(w => w.GetDbContext()).Returns(_db.Object);
        _writeCtx.Setup(w => w.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _db.Setup(d => d.Set<SignalRejectionAudit>())
            .Returns(new List<SignalRejectionAudit>().AsQueryable().BuildMockDbSet().Object);

        var scope = new Mock<IServiceScope>();
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_writeCtx.Object);
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        _scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        _auditor = new SignalRejectionAuditor(_scopeFactory.Object, _metrics, Mock.Of<ILogger<SignalRejectionAuditor>>());
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task RecordAsync_PersistsRowAndIncrementsMetric()
    {
        using var counter = new CounterProbe(_meterFactory, "trading.signals.rejections_audited");

        await _auditor.RecordAsync(
            stage: "MTF",
            reason: "mtf_not_confirmed",
            symbol: "EURUSD",
            source: "StrategyWorker",
            strategyId: 42,
            tradeSignalId: 99,
            detail: "H4 disagreed with H1");

        _db.Verify(d => d.Set<SignalRejectionAudit>(), Times.AtLeastOnce);
        _writeCtx.Verify(w => w.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1L, counter.Total);
        Assert.Contains("MTF", counter.TagValues("stage"));
        Assert.Contains("mtf_not_confirmed", counter.TagValues("reason"));
    }

    [Fact]
    public async Task RecordAsync_SwallowsDbFailures_DoesNotRethrow()
    {
        // Make SaveChangesAsync throw — the auditor must NOT surface this to callers.
        _writeCtx
            .Setup(w => w.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated DB crash"));

        using var counter = new CounterProbe(_meterFactory, "trading.signals.rejections_audited");

        await _auditor.RecordAsync("Tier1", "some_reason", "EURUSD", "SignalOrderBridgeWorker");

        // Counter must NOT fire on failure — we never claimed to have written the row.
        Assert.Equal(0L, counter.Total);
    }

    [Fact]
    public async Task RecordAsync_TruncatesOverlongFields()
    {
        // Capture the row the auditor adds so we can inspect it.
        SignalRejectionAudit? captured = null;
        var set = new Mock<DbSet<SignalRejectionAudit>>();
        set.Setup(s => s.Add(It.IsAny<SignalRejectionAudit>()))
            .Callback<SignalRejectionAudit>(r => captured = r);
        _db.Setup(d => d.Set<SignalRejectionAudit>()).Returns(set.Object);

        var longStage  = new string('s', 100);
        var longReason = new string('r', 200);
        var longDetail = new string('d', 5000);

        await _auditor.RecordAsync(
            stage: longStage,
            reason: longReason,
            symbol: "EURUSD",
            source: "worker",
            detail: longDetail);

        Assert.NotNull(captured);
        Assert.Equal(32, captured!.Stage.Length);
        Assert.Equal(64, captured.Reason.Length);
        Assert.Equal(2000, captured.Detail!.Length);
    }

    [Fact]
    public async Task RecordAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _writeCtx
            .Setup(w => w.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => ct.IsCancellationRequested
                ? Task.FromException<int>(new OperationCanceledException(ct))
                : Task.FromResult(1));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _auditor.RecordAsync("Test", "test_reason", "EURUSD", "Test", ct: cts.Token));
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
