using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class CalibrationSnapshotWorkerTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly Mock<IReadApplicationDbContext> _readCtx = new();
    private readonly Mock<IWriteApplicationDbContext> _writeCtx = new();
    private readonly Mock<DbContext> _db = new();
    private readonly List<CalibrationSnapshot> _existingSnapshots = new();
    private readonly List<CalibrationSnapshot> _writtenSnapshots = new();

    public CalibrationSnapshotWorkerTest()
    {
        _metrics = new TradingMetrics(_meterFactory);

        _readCtx.Setup(c => c.GetDbContext()).Returns(_db.Object);
        _writeCtx.Setup(c => c.GetDbContext()).Returns(_db.Object);
        _writeCtx.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        SetEngineConfig([]);

        // Existing snapshots — empty by default, per-test overrides re-bind.
        RebindSnapshots();

        // DbSet<CalibrationSnapshot>.Add captures into _writtenSnapshots.
        var scope = new Mock<IServiceScope>();
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_readCtx.Object);
        provider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_writeCtx.Object);
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        _scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task RunCycleAsync_AggregatesRejectionsPerStageReason_WritesExpectedRows()
    {
        // Anchor "now" to mid-month so the previous month has a clean [start,end) window.
        _timeProvider.SetNow(new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc));

        // Rejections in March 2026 (= previous complete month).
        var mar = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        SetRejections(
            MakeRejection(mar.AddDays(1),  strategyId: 10, symbol: "EURUSD", stage: "Regime", reason: "regime_blocked"),
            MakeRejection(mar.AddDays(2),  strategyId: 10, symbol: "EURUSD", stage: "Regime", reason: "regime_blocked"),
            MakeRejection(mar.AddDays(5),  strategyId: 11, symbol: "GBPUSD", stage: "Regime", reason: "regime_blocked"),
            MakeRejection(mar.AddDays(10), strategyId: 10, symbol: "EURUSD", stage: "MTF",    reason: "mtf_not_confirmed"));

        var worker = NewWorker();
        var result = await worker.RunCycleAsync(CancellationToken.None);

        // Expect 2 rows for March: (Regime,regime_blocked,count=3,symbols=2,strategies=2)
        //                          (MTF,mtf_not_confirmed,count=1,symbols=1,strategies=1)
        Assert.Equal(2, _writtenSnapshots.Count);
        Assert.Equal(1, result.MonthsProcessed);
        Assert.Equal(0, result.MonthsSkippedAlreadyExists);
        Assert.Equal(5, result.MonthsSkippedEmpty);   // the other 5 months in the default 6-month window are empty
        Assert.Equal(0, result.MonthsFailed);
        Assert.Equal(2L, result.SnapshotsWritten);

        var regimeRow = _writtenSnapshots.Single(s => s.Stage == "Regime" && s.Reason == "regime_blocked");
        Assert.Equal(3L, regimeRow.RejectionCount);
        Assert.Equal(2,  regimeRow.DistinctSymbols);
        Assert.Equal(2,  regimeRow.DistinctStrategies);
        Assert.Equal(mar, regimeRow.PeriodStart);
        Assert.Equal(mar.AddMonths(1), regimeRow.PeriodEnd);
        Assert.Equal(CalibrationSnapshotWorker.GranularityMonthly, regimeRow.PeriodGranularity);

        var mtfRow = _writtenSnapshots.Single(s => s.Stage == "MTF" && s.Reason == "mtf_not_confirmed");
        Assert.Equal(1L, mtfRow.RejectionCount);
        Assert.Equal(1,  mtfRow.DistinctSymbols);
        Assert.Equal(1,  mtfRow.DistinctStrategies);
    }

    [Fact]
    public async Task RunCycleAsync_SkipsMonthsAlreadySnapshotted()
    {
        _timeProvider.SetNow(new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc));

        var mar = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        // Pre-populate an existing snapshot for March so the worker must skip it.
        _existingSnapshots.Add(new CalibrationSnapshot
        {
            PeriodStart = mar, PeriodEnd = mar.AddMonths(1),
            PeriodGranularity = CalibrationSnapshotWorker.GranularityMonthly,
            Stage = "Regime", Reason = "regime_blocked", RejectionCount = 999,
        });
        RebindSnapshots();

        SetRejections(MakeRejection(mar.AddDays(5), 1, "EURUSD", "Regime", "regime_blocked"));

        var worker = NewWorker();
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(_writtenSnapshots);
        Assert.Equal(0, result.MonthsProcessed);
        Assert.Equal(1, result.MonthsSkippedAlreadyExists);  // March
        Assert.Equal(5, result.MonthsSkippedEmpty);          // the other 5 months in the window
        Assert.Equal(0L, result.SnapshotsWritten);
    }

    [Fact]
    public async Task RunCycleAsync_EmptyRejections_WritesNothing()
    {
        _timeProvider.SetNow(new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc));
        SetRejections();

        var worker = NewWorker();
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(_writtenSnapshots);
        Assert.Equal(0, result.MonthsProcessed);
        Assert.Equal(0, result.MonthsSkippedAlreadyExists);
        Assert.Equal(6, result.MonthsSkippedEmpty);
        Assert.Equal(0, result.MonthsFailed);
    }

    [Fact]
    public async Task RunCycleAsync_ExcludesCurrentMonth_OnlyProcessesCompletePeriods()
    {
        _timeProvider.SetNow(new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc));

        // Rejection in April (current month) — must NOT be snapshotted.
        var apr = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc);
        SetRejections(MakeRejection(apr, 1, "EURUSD", "Regime", "regime_blocked"));

        var worker = NewWorker();
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(_writtenSnapshots);
        Assert.Equal(0, result.MonthsProcessed);
    }

    [Fact]
    public async Task RunCycleAsync_MultiMonthBackfill_WritesOneBatchPerCompleteMonth()
    {
        _timeProvider.SetNow(new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc));

        var mar = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var feb = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        SetRejections(
            MakeRejection(mar.AddDays(3), 10, "EURUSD", "Regime", "regime_blocked"),
            MakeRejection(feb.AddDays(7), 11, "GBPUSD", "MTF",    "mtf_not_confirmed"));

        var worker = NewWorker();
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(2, _writtenSnapshots.Count);
        Assert.Equal(2, result.MonthsProcessed);
        Assert.Equal(0, result.MonthsSkippedAlreadyExists);
        Assert.Equal(4, result.MonthsSkippedEmpty);    // 6-month window, 2 processed, 4 empty
        Assert.Equal(2L, result.SnapshotsWritten);

        Assert.Single(_writtenSnapshots, s => s.PeriodStart == mar && s.Stage == "Regime");
        Assert.Single(_writtenSnapshots, s => s.PeriodStart == feb && s.Stage == "MTF");
    }

    [Fact]
    public async Task RunCycleAsync_BackfillMonthsConfig_HonouredSoOlderMonthsAreIgnored()
    {
        _timeProvider.SetNow(new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc));
        SetEngineConfig(new EngineConfig { Key = "Calibration:BackfillMonths", Value = "1" });

        var mar = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var feb = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        SetRejections(
            MakeRejection(mar.AddDays(3), 1, "EURUSD", "Regime", "regime_blocked"),
            MakeRejection(feb.AddDays(7), 2, "GBPUSD", "MTF",    "mtf_not_confirmed"));

        var worker = NewWorker();
        var result = await worker.RunCycleAsync(CancellationToken.None);

        // Only March (the single complete month) is in the window — Feb must be ignored.
        Assert.Single(_writtenSnapshots);
        Assert.Equal(mar, _writtenSnapshots[0].PeriodStart);
        Assert.Equal(1, result.MonthsProcessed);
        Assert.Equal(0, result.MonthsSkippedAlreadyExists);
        Assert.Equal(0, result.MonthsSkippedEmpty);
    }

    [Fact]
    public async Task RunCycleAsync_BackfillMonthsConfig_ClampedToMinimumOfOne()
    {
        _timeProvider.SetNow(new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc));
        SetEngineConfig(new EngineConfig { Key = "Calibration:BackfillMonths", Value = "0" });

        var mar = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        SetRejections(MakeRejection(mar.AddDays(3), 1, "EURUSD", "Regime", "regime_blocked"));

        var worker = NewWorker();
        var result = await worker.RunCycleAsync(CancellationToken.None);

        // 0 is clamped up to 1, so March still gets processed.
        Assert.Single(_writtenSnapshots);
        Assert.Equal(1, result.MonthsProcessed);
    }

    [Fact]
    public async Task RunCycleAsync_MonthFailure_LogsAndContinuesWithRemainingMonths()
    {
        _timeProvider.SetNow(new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc));

        var mar = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var feb = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        SetRejections(
            MakeRejection(mar.AddDays(3), 10, "EURUSD", "Regime", "regime_blocked"),
            MakeRejection(feb.AddDays(7), 11, "GBPUSD", "MTF",    "mtf_not_confirmed"));

        // March is processed first (i=1). Its SaveChangesAsync throws; February's must still run.
        _writeCtx.SetupSequence(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated DB failure"))
            .ReturnsAsync(1);

        var worker = NewWorker();
        var result = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, result.MonthsProcessed);            // Feb succeeded
        Assert.Equal(1, result.MonthsFailed);               // Mar failed
        Assert.Equal(0, result.MonthsSkippedAlreadyExists);
        Assert.Equal(4, result.MonthsSkippedEmpty);         // 4 empty months in the rest of the window
        Assert.Equal(1L, result.SnapshotsWritten);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private CalibrationSnapshotWorker NewWorker() => new(
        _scopeFactory.Object,
        Mock.Of<ILogger<CalibrationSnapshotWorker>>(),
        _metrics,
        _timeProvider);

    private void SetRejections(params SignalRejectionAudit[] rows)
    {
        _db.Setup(d => d.Set<SignalRejectionAudit>())
           .Returns(rows.AsQueryable().BuildMockDbSet().Object);
    }

    private void SetEngineConfig(params EngineConfig[] rows)
    {
        _db.Setup(d => d.Set<EngineConfig>())
           .Returns(rows.AsQueryable().BuildMockDbSet().Object);
    }

    private void RebindSnapshots()
    {
        var snapshotSet = new Mock<DbSet<CalibrationSnapshot>>();
        var queryable = _existingSnapshots.AsQueryable().BuildMockDbSet();
        snapshotSet.As<IQueryable<CalibrationSnapshot>>().Setup(q => q.Provider).Returns(queryable.Object.AsQueryable().Provider);
        snapshotSet.As<IQueryable<CalibrationSnapshot>>().Setup(q => q.Expression).Returns(queryable.Object.AsQueryable().Expression);
        snapshotSet.As<IQueryable<CalibrationSnapshot>>().Setup(q => q.ElementType).Returns(queryable.Object.AsQueryable().ElementType);
        snapshotSet.As<IQueryable<CalibrationSnapshot>>().Setup(q => q.GetEnumerator()).Returns(() => queryable.Object.AsQueryable().GetEnumerator());

        // Delegate Add so the worker's writes are captured.
        snapshotSet.Setup(s => s.Add(It.IsAny<CalibrationSnapshot>()))
            .Callback<CalibrationSnapshot>(r => _writtenSnapshots.Add(r));

        // Rebuild the mock-queryable to honour AnyAsync with the pre-existing set.
        _db.Setup(d => d.Set<CalibrationSnapshot>())
           .Returns(_existingSnapshots.AsQueryable().BuildMockDbSet(add: r => _writtenSnapshots.Add(r)).Object);
    }

    private static SignalRejectionAudit MakeRejection(
        DateTime at, long strategyId, string symbol, string stage, string reason)
        => new()
        {
            RejectedAt = at,
            StrategyId = strategyId,
            Symbol     = symbol,
            Stage      = stage,
            Reason     = reason,
            Source     = "TestWorker",
        };

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void SetNow(DateTime utc) => _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}

/// <summary>
/// Local extension that re-binds MockQueryable's BuildMockDbSet to run a
/// callback when Add() is called. MockQueryable's stock overload doesn't expose
/// that seam, so we provide one here.
/// </summary>
internal static class MockQueryableExtensions
{
    public static Mock<DbSet<T>> BuildMockDbSet<T>(this IQueryable<T> source, Action<T> add) where T : class
    {
        var mock = source.BuildMockDbSet();
        mock.Setup(m => m.Add(It.IsAny<T>())).Callback<T>(add);
        return mock;
    }
}
