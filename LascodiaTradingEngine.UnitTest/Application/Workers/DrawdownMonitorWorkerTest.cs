using System.Diagnostics.Metrics;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.DrawdownRecovery.Commands.RecordDrawdownSnapshot;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class DrawdownMonitorWorkerTest
{
    private readonly Mock<ILogger<DrawdownMonitorWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IMediator> _mockMediator;
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly FakeTimeProvider _timeProvider;
    private readonly DrawdownMonitorWorker _worker;

    public DrawdownMonitorWorkerTest()
    {
        _mockLogger       = new Mock<ILogger<DrawdownMonitorWorker>>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockMediator     = new Mock<IMediator>();
        _mockReadContext  = new Mock<IReadApplicationDbContext>();
        _timeProvider     = new FakeTimeProvider(new DateTimeOffset(2026, 4, 24, 10, 0, 0, TimeSpan.Zero));

        var mockScope    = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();

        mockProvider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mockMediator.Object);
        mockProvider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_mockReadContext.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        _mockMediator
            .Setup(m => m.Send(It.IsAny<RecordDrawdownSnapshotCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<string>.Init("Normal", true, "Successful", "00"));

        _worker = new DrawdownMonitorWorker(
            _mockLogger.Object,
            _mockScopeFactory.Object,
            new TradingMetrics(new TestMeterFactory()),
            _timeProvider);
    }

    private void SetupDbContext(
        List<TradingAccount> accounts,
        List<DrawdownSnapshot> snapshots,
        List<EngineConfig>? configs = null)
    {
        var mockDbContext = new Mock<DbContext>();
        var accountDbSet  = accounts.AsQueryable().BuildMockDbSet();
        var snapshotDbSet = snapshots.AsQueryable().BuildMockDbSet();
        var configDbSet   = (configs ?? []).AsQueryable().BuildMockDbSet();

        mockDbContext.Setup(c => c.Set<TradingAccount>()).Returns(accountDbSet.Object);
        mockDbContext.Setup(c => c.Set<DrawdownSnapshot>()).Returns(snapshotDbSet.Object);
        mockDbContext.Setup(c => c.Set<EngineConfig>()).Returns(configDbSet.Object);

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
    }

    [Fact]
    public async Task RunCycle_NoActiveAccount_SkipsQuietly()
    {
        SetupDbContext(
            accounts: [],
            snapshots: []);

        await _worker.RunCycleAsync(CancellationToken.None);

        _mockMediator.Verify(
            m => m.Send(It.IsAny<RecordDrawdownSnapshotCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycle_FirstSnapshotEver_PeakEqualsCurrent()
    {
        var account = new TradingAccount
        {
            Id = 1,
            Equity = 10_000m,
            MarginUsed = 0m,
            IsActive = true,
            IsDeleted = false,
            Currency = "USD"
        };

        SetupDbContext(
            accounts: [account],
            snapshots: []);

        await _worker.RunCycleAsync(CancellationToken.None);

        _mockMediator.Verify(
            m => m.Send(
                It.Is<RecordDrawdownSnapshotCommand>(c =>
                    c.CurrentEquity == 10_000m && c.PeakEquity == 10_000m),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCycle_EquityAbovePeak_PeakUpdated()
    {
        var account = new TradingAccount
        {
            Id = 1,
            Equity = 12_000m,
            MarginUsed = 0m,
            IsActive = true,
            IsDeleted = false,
            Currency = "USD"
        };

        var latestSnapshot = new DrawdownSnapshot
        {
            Id = 1,
            PeakEquity = 10_000m,
            CurrentEquity = 10_000m,
            RecordedAt = DateTime.UtcNow.AddMinutes(-1)
        };

        SetupDbContext(
            accounts: [account],
            snapshots: [latestSnapshot]);

        await _worker.RunCycleAsync(CancellationToken.None);

        _mockMediator.Verify(
            m => m.Send(
                It.Is<RecordDrawdownSnapshotCommand>(c =>
                    c.CurrentEquity == 12_000m && c.PeakEquity == 12_000m),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCycle_EquityBelowPeak_PeakPreserved()
    {
        var account = new TradingAccount
        {
            Id = 1,
            Equity = 8_000m,
            MarginUsed = 0m,
            IsActive = true,
            IsDeleted = false,
            Currency = "USD"
        };

        var latestSnapshot = new DrawdownSnapshot
        {
            Id = 1,
            PeakEquity = 10_000m,
            CurrentEquity = 9_500m,
            RecordedAt = DateTime.UtcNow.AddMinutes(-1)
        };

        SetupDbContext(
            accounts: [account],
            snapshots: [latestSnapshot]);

        await _worker.RunCycleAsync(CancellationToken.None);

        _mockMediator.Verify(
            m => m.Send(
                It.Is<RecordDrawdownSnapshotCommand>(c =>
                    c.CurrentEquity == 8_000m && c.PeakEquity == 10_000m),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCycle_ZeroOrNegativeEquity_SkipsSnapshot()
    {
        var account = new TradingAccount
        {
            Id = 1,
            Equity = 0m,
            MarginUsed = 0m,
            IsActive = true,
            IsDeleted = false,
            Currency = "USD"
        };

        SetupDbContext(
            accounts: [account],
            snapshots: []);

        await _worker.RunCycleAsync(CancellationToken.None);

        _mockMediator.Verify(
            m => m.Send(It.IsAny<RecordDrawdownSnapshotCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycle_MultipleActiveAccounts_SkipsToAvoidAmbiguousGlobalSnapshot()
    {
        SetupDbContext(
            accounts:
            [
                new TradingAccount { Id = 1, Equity = 10_000m, IsActive = true, IsDeleted = false, Currency = "USD" },
                new TradingAccount { Id = 2, Equity = 11_000m, IsActive = true, IsDeleted = false, Currency = "USD" }
            ],
            snapshots: []);

        await _worker.RunCycleAsync(CancellationToken.None);

        _mockMediator.Verify(
            m => m.Send(It.IsAny<RecordDrawdownSnapshotCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReadPollIntervalSecondsAsync_LegacyKey_FallsBack()
    {
        SetupDbContext(
            accounts: [],
            snapshots: [],
            configs:
            [
                new EngineConfig
                {
                    Key = "DrawdownMonitor:IntervalSeconds",
                    Value = "45",
                    IsDeleted = false
                }
            ]);

        int pollSecs = await _worker.ReadPollIntervalSecondsAsync(CancellationToken.None);

        Assert.Equal(45, pollSecs);
    }

    [Fact]
    public async Task Handle_LargeLoss_TriggersEmergencySnapshot()
    {
        SetupDbContext(
            accounts:
            [
                new TradingAccount { Id = 1, Equity = 10_000m, MarginUsed = 0m, IsActive = true, IsDeleted = false, Currency = "USD" }
            ],
            snapshots: []);

        await _worker.Handle(new PositionClosedIntegrationEvent
        {
            PositionId = 42,
            Symbol = "EURUSD",
            RealisedPnL = -250m,
            WasProfitable = false
        });

        _mockMediator.Verify(
            m => m.Send(
                It.Is<RecordDrawdownSnapshotCommand>(c =>
                    c.CurrentEquity == 10_000m && c.PeakEquity == 10_000m),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_SecondEmergencyInsideCooldown_SkipsDuplicateSnapshot()
    {
        SetupDbContext(
            accounts:
            [
                new TradingAccount { Id = 1, Equity = 10_000m, MarginUsed = 0m, IsActive = true, IsDeleted = false, Currency = "USD" }
            ],
            snapshots: []);

        var @event = new PositionClosedIntegrationEvent
        {
            PositionId = 42,
            Symbol = "EURUSD",
            RealisedPnL = -250m,
            WasProfitable = false
        };

        await _worker.Handle(@event);
        _timeProvider.Advance(TimeSpan.FromSeconds(5));
        await _worker.Handle(@event);
        _timeProvider.Advance(TimeSpan.FromSeconds(6));
        await _worker.Handle(@event);

        _mockMediator.Verify(
            m => m.Send(It.IsAny<RecordDrawdownSnapshotCommand>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Handle_FailedEmergencySnapshot_DoesNotStartCooldown()
    {
        SetupDbContext(
            accounts:
            [
                new TradingAccount { Id = 1, Equity = 10_000m, MarginUsed = 0m, IsActive = true, IsDeleted = false, Currency = "USD" }
            ],
            snapshots: []);

        _mockMediator
            .SetupSequence(m => m.Send(It.IsAny<RecordDrawdownSnapshotCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("write failed"))
            .ReturnsAsync(ResponseData<string>.Init("Normal", true, "Successful", "00"));

        var @event = new PositionClosedIntegrationEvent
        {
            PositionId = 42,
            Symbol = "EURUSD",
            RealisedPnL = -250m,
            WasProfitable = false
        };

        await _worker.Handle(@event);
        await _worker.Handle(@event);

        _mockMediator.Verify(
            m => m.Send(It.IsAny<RecordDrawdownSnapshotCommand>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
    }
}

file sealed class TestMeterFactory : IMeterFactory
{
    public Meter Create(MeterOptions options) => new(options);
    public void Dispose() { }
}
