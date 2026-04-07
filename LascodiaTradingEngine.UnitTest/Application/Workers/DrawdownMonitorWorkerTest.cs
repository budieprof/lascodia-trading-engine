using System.Diagnostics.Metrics;
using System.Reflection;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Diagnostics;
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
    private readonly DrawdownMonitorWorker _worker;

    public DrawdownMonitorWorkerTest()
    {
        _mockLogger       = new Mock<ILogger<DrawdownMonitorWorker>>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockMediator     = new Mock<IMediator>();
        _mockReadContext  = new Mock<IReadApplicationDbContext>();

        // Wire up IServiceScopeFactory -> IServiceScope -> IServiceProvider
        var mockScope    = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();

        mockProvider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mockMediator.Object);
        mockProvider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_mockReadContext.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        // Default: mediator returns success for RecordDrawdownSnapshotCommand
        _mockMediator
            .Setup(m => m.Send(It.IsAny<RecordDrawdownSnapshotCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<string>.Init("Normal", true, "Successful", "00"));

        _worker = new DrawdownMonitorWorker(_mockLogger.Object, _mockScopeFactory.Object, new TradingMetrics(new TestMeterFactory()));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupDbContext(List<TradingAccount> accounts, List<DrawdownSnapshot> snapshots)
    {
        var mockDbContext      = new Mock<DbContext>();
        var accountDbSet       = accounts.AsQueryable().BuildMockDbSet();
        var snapshotDbSet      = snapshots.AsQueryable().BuildMockDbSet();

        mockDbContext.Setup(c => c.Set<TradingAccount>()).Returns(accountDbSet.Object);
        mockDbContext.Setup(c => c.Set<DrawdownSnapshot>()).Returns(snapshotDbSet.Object);

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
    }

    /// <summary>
    /// Invokes the private RecordSnapshotAsync method directly to avoid
    /// the polling loop and Task.Delay in ExecuteAsync.
    /// </summary>
    private async Task InvokeRecordSnapshotAsync(CancellationToken ct = default)
    {
        var method = typeof(DrawdownMonitorWorker)
            .GetMethod("RecordSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(_worker, new object[] { ct })!;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordSnapshot_NoActiveAccount_SkipsQuietly()
    {
        // Arrange — no active trading accounts
        SetupDbContext(
            accounts:  new List<TradingAccount>(),
            snapshots: new List<DrawdownSnapshot>());

        // Act
        await InvokeRecordSnapshotAsync();

        // Assert — no command should be sent
        _mockMediator.Verify(
            m => m.Send(It.IsAny<RecordDrawdownSnapshotCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RecordSnapshot_FirstSnapshotEver_PeakEqualsCurrent()
    {
        // Arrange — active account with equity 10_000, no prior snapshots
        var account = new TradingAccount
        {
            Id = 1, Equity = 10_000m, MarginUsed = 0m,
            IsActive = true, IsDeleted = false, Currency = "USD"
        };

        SetupDbContext(
            accounts:  new List<TradingAccount> { account },
            snapshots: new List<DrawdownSnapshot>());

        // Act
        await InvokeRecordSnapshotAsync();

        // Assert — PeakEquity == CurrentEquity since no prior snapshot exists
        _mockMediator.Verify(
            m => m.Send(
                It.Is<RecordDrawdownSnapshotCommand>(c =>
                    c.CurrentEquity == 10_000m && c.PeakEquity == 10_000m),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordSnapshot_EquityAbovePeak_PeakUpdated()
    {
        // Arrange — equity (12_000) is above the stored peak (10_000)
        var account = new TradingAccount
        {
            Id = 1, Equity = 12_000m, MarginUsed = 0m,
            IsActive = true, IsDeleted = false, Currency = "USD"
        };

        var latestSnapshot = new DrawdownSnapshot
        {
            Id = 1, PeakEquity = 10_000m, CurrentEquity = 10_000m,
            RecordedAt = DateTime.UtcNow.AddMinutes(-1)
        };

        SetupDbContext(
            accounts:  new List<TradingAccount> { account },
            snapshots: new List<DrawdownSnapshot> { latestSnapshot });

        // Act
        await InvokeRecordSnapshotAsync();

        // Assert — PeakEquity should be updated to 12_000 (max of stored 10_000 and current 12_000)
        _mockMediator.Verify(
            m => m.Send(
                It.Is<RecordDrawdownSnapshotCommand>(c =>
                    c.CurrentEquity == 12_000m && c.PeakEquity == 12_000m),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordSnapshot_EquityBelowPeak_PeakPreserved()
    {
        // Arrange — equity (8_000) is below stored peak (10_000)
        var account = new TradingAccount
        {
            Id = 1, Equity = 8_000m, MarginUsed = 0m,
            IsActive = true, IsDeleted = false, Currency = "USD"
        };

        var latestSnapshot = new DrawdownSnapshot
        {
            Id = 1, PeakEquity = 10_000m, CurrentEquity = 9_500m,
            RecordedAt = DateTime.UtcNow.AddMinutes(-1)
        };

        SetupDbContext(
            accounts:  new List<TradingAccount> { account },
            snapshots: new List<DrawdownSnapshot> { latestSnapshot });

        // Act
        await InvokeRecordSnapshotAsync();

        // Assert — PeakEquity should remain 10_000 (max of stored 10_000 and current 8_000)
        _mockMediator.Verify(
            m => m.Send(
                It.Is<RecordDrawdownSnapshotCommand>(c =>
                    c.CurrentEquity == 8_000m && c.PeakEquity == 10_000m),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordSnapshot_ZeroPeakEquity_Skips()
    {
        // Arrange — account equity is 0 and no prior snapshot exists,
        // so peakEquity = currentEquity = 0 which triggers the zero guard
        var account = new TradingAccount
        {
            Id = 1, Equity = 0m, MarginUsed = 0m,
            IsActive = true, IsDeleted = false, Currency = "USD"
        };

        SetupDbContext(
            accounts:  new List<TradingAccount> { account },
            snapshots: new List<DrawdownSnapshot>());

        // Act
        await InvokeRecordSnapshotAsync();

        // Assert — no command should be sent because peakEquity <= 0
        _mockMediator.Verify(
            m => m.Send(It.IsAny<RecordDrawdownSnapshotCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

file class TestMeterFactory : IMeterFactory
{
    public Meter Create(MeterOptions options) => new(options);
    public void Dispose() { }
}
