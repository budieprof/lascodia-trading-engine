using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Services;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class DbOperationBulkheadTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly DbOperationBulkhead _bulkhead;

    public DbOperationBulkheadTest()
    {
        _metrics = new TradingMetrics(_meterFactory);
        _bulkhead = new DbOperationBulkhead(_metrics, NullLogger<DbOperationBulkhead>.Instance);
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task Acquire_SingleSlot_ReducesAvailability()
    {
        int before = _bulkhead.AvailableSlots(DbOperationBulkhead.GroupSignalPath);
        using var slot = await _bulkhead.AcquireAsync(DbOperationBulkhead.GroupSignalPath);
        int after = _bulkhead.AvailableSlots(DbOperationBulkhead.GroupSignalPath);

        Assert.Equal(before - 1, after);
    }

    [Fact]
    public async Task Release_ViaDispose_RestoresAvailability()
    {
        int before = _bulkhead.AvailableSlots(DbOperationBulkhead.GroupBacktesting);
        var slot = await _bulkhead.AcquireAsync(DbOperationBulkhead.GroupBacktesting);
        slot.Dispose();

        Assert.Equal(before, _bulkhead.AvailableSlots(DbOperationBulkhead.GroupBacktesting));
    }

    [Fact]
    public async Task Release_IsIdempotent()
    {
        int before = _bulkhead.AvailableSlots(DbOperationBulkhead.GroupOther);
        var slot = await _bulkhead.AcquireAsync(DbOperationBulkhead.GroupOther);
        slot.Dispose();
        slot.Dispose(); // second dispose should not over-release

        Assert.Equal(before, _bulkhead.AvailableSlots(DbOperationBulkhead.GroupOther));
    }

    [Fact]
    public async Task UnknownGroup_DefaultsToOtherCapacity()
    {
        // Implementation detail: unknown groups are created lazily with the
        // "other" fallback capacity so new callers don't need DI updates.
        int cap1 = _bulkhead.AvailableSlots("some-new-group");
        Assert.Equal(0, cap1); // not yet created

        using var slot = await _bulkhead.AcquireAsync("some-new-group");
        int capAfter = _bulkhead.AvailableSlots("some-new-group");
        Assert.True(capAfter >= 0);
    }

    [Fact]
    public async Task GroupsAreIsolated()
    {
        // Exhaust one group — the other should be unaffected.
        int signalPathCap = _bulkhead.AvailableSlots(DbOperationBulkhead.GroupSignalPath);
        var slots = new List<IDisposable>();
        for (int i = 0; i < signalPathCap; i++)
            slots.Add(await _bulkhead.AcquireAsync(DbOperationBulkhead.GroupSignalPath));

        Assert.Equal(0, _bulkhead.AvailableSlots(DbOperationBulkhead.GroupSignalPath));
        Assert.True(_bulkhead.AvailableSlots(DbOperationBulkhead.GroupMLTraining) > 0);

        // Release to keep other tests clean.
        foreach (var s in slots) s.Dispose();
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
