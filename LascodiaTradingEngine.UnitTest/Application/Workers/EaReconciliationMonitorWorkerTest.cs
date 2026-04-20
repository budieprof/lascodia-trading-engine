using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class EaReconciliationMonitorWorkerTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly Mock<IReadApplicationDbContext> _readCtx = new();
    private readonly Mock<DbContext> _db = new();
    private readonly Mock<IAlertDispatcher> _dispatcher = new();
    private readonly Mock<IServiceScope> _scope = new();

    public EaReconciliationMonitorWorkerTest()
    {
        _metrics = new TradingMetrics(_meterFactory);

        _readCtx.Setup(c => c.GetDbContext()).Returns(_db.Object);

        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_readCtx.Object);
        provider.Setup(p => p.GetService(typeof(IAlertDispatcher))).Returns(_dispatcher.Object);
        _scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        _scopeFactory.Setup(f => f.CreateScope()).Returns(_scope.Object);

        _timeProvider.SetNow(new DateTime(2026, 4, 20, 12, 0, 0, DateTimeKind.Utc));
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task EmptyWindow_DoesNotAlert_ReturnsEmptyAggregate()
    {
        SetRuns();

        var aggregate = await NewWorker().RunCycleAsync(_scope.Object, windowMinutes: 30, meanAlertThreshold: 3, CancellationToken.None);

        Assert.Equal(0, aggregate.RunCount);
        _dispatcher.Verify(d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MeanDriftBelowThreshold_DoesNotAlert()
    {
        SetRuns(
            MakeRun(_timeProvider.Now.AddMinutes(-5),  totalDrift: 1),
            MakeRun(_timeProvider.Now.AddMinutes(-10), totalDrift: 2),
            MakeRun(_timeProvider.Now.AddMinutes(-15), totalDrift: 0));

        var aggregate = await NewWorker().RunCycleAsync(_scope.Object, windowMinutes: 30, meanAlertThreshold: 5, CancellationToken.None);

        Assert.Equal(3, aggregate.RunCount);
        Assert.True(aggregate.MeanDriftPerRun < 5);
        _dispatcher.Verify(d => d.DispatchAsync(It.IsAny<Alert>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MeanDriftAboveThreshold_DispatchesAlert()
    {
        SetRuns(
            MakeRun(_timeProvider.Now.AddMinutes(-5),  totalDrift: 5, orphanedEnginePositions: 3, unknownBrokerPositions: 2),
            MakeRun(_timeProvider.Now.AddMinutes(-10), totalDrift: 4, orphanedEnginePositions: 1, unknownBrokerPositions: 3),
            MakeRun(_timeProvider.Now.AddMinutes(-15), totalDrift: 6, orphanedEnginePositions: 2, unknownBrokerPositions: 4));

        var aggregate = await NewWorker().RunCycleAsync(_scope.Object, windowMinutes: 30, meanAlertThreshold: 3, CancellationToken.None);

        Assert.Equal(3, aggregate.RunCount);
        Assert.True(aggregate.MeanDriftPerRun >= 3);
        _dispatcher.Verify(d => d.DispatchAsync(
            It.Is<Alert>(a => a.AlertType == LascodiaTradingEngine.Domain.Enums.AlertType.DataQualityIssue),
            It.Is<string>(s => s.Contains("drift alert", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunsOutsideWindow_AreIgnored()
    {
        SetRuns(
            MakeRun(_timeProvider.Now.AddMinutes(-5),  totalDrift: 10), // inside
            MakeRun(_timeProvider.Now.AddMinutes(-60), totalDrift: 50)); // outside 30m window

        var aggregate = await NewWorker().RunCycleAsync(_scope.Object, windowMinutes: 30, meanAlertThreshold: 3, CancellationToken.None);

        Assert.Equal(1, aggregate.RunCount);
        Assert.Equal(10, aggregate.MeanDriftPerRun, precision: 3);
    }

    // ── Helpers ──

    private EaReconciliationMonitorWorker NewWorker() =>
        new(_scopeFactory.Object, NullLogger<EaReconciliationMonitorWorker>.Instance, _metrics, _timeProvider);

    private void SetRuns(params ReconciliationRun[] rows)
    {
        _db.Setup(d => d.Set<ReconciliationRun>()).Returns(rows.AsQueryable().BuildMockDbSet().Object);
    }

    private static ReconciliationRun MakeRun(
        DateTime at,
        int totalDrift = 0,
        int orphanedEnginePositions = 0,
        int unknownBrokerPositions  = 0,
        int mismatched              = 0,
        int orphanedEngineOrders    = 0,
        int unknownBrokerOrders     = 0)
    {
        // Defaults satisfy totalDrift ≥ sum-of-parts so the caller's totalDrift
        // doesn't get double-counted when the worker recomputes.
        int sum = orphanedEnginePositions + unknownBrokerPositions + mismatched + orphanedEngineOrders + unknownBrokerOrders;
        return new ReconciliationRun
        {
            InstanceId              = "ea1",
            RunAt                   = at,
            OrphanedEnginePositions = orphanedEnginePositions,
            UnknownBrokerPositions  = unknownBrokerPositions,
            MismatchedPositions     = mismatched,
            OrphanedEngineOrders    = orphanedEngineOrders,
            UnknownBrokerOrders     = unknownBrokerOrders,
            TotalDrift              = sum == 0 ? totalDrift : sum,
        };
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public DateTime Now => _now.UtcDateTime;
        public void SetNow(DateTime utc) => _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
