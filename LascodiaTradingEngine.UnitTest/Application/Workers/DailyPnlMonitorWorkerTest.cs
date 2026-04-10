using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.EmergencyFlatten.Commands.EmergencyFlatten;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.UnitTest.TestHelpers;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class DailyPnlMonitorWorkerTest
{
    private static (DailyPnlMonitorWorker worker, Mock<IMediator> mediator) CreateWorkerWithScope(
        List<TradingAccount> accounts,
        List<AccountPerformanceAttribution> attributions,
        List<DrawdownSnapshot> snapshots)
    {
        var mockDbContext = new Mock<DbContext>();
        var mockAccountSet = accounts.AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<TradingAccount>()).Returns(mockAccountSet.Object);

        var mockAttributionSet = attributions.AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<AccountPerformanceAttribution>()).Returns(mockAttributionSet.Object);

        var mockSnapshotSet = snapshots.AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<DrawdownSnapshot>()).Returns(mockSnapshotSet.Object);

        var mockReadContext = new Mock<IReadApplicationDbContext>();
        mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        // Write context for EngineConfig persistence (flatten dedup)
        var mockWriteDbContext = new Mock<DbContext>();
        var mockEngineConfigSet = new List<EngineConfig>().AsQueryable().BuildMockDbSet();
        mockWriteDbContext.Setup(c => c.Set<EngineConfig>()).Returns(mockEngineConfigSet.Object);
        mockWriteDbContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        var mockWriteContext = new Mock<IWriteApplicationDbContext>();
        mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockWriteDbContext.Object);
        mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        // Also add EngineConfig set to read context for the flatten dedup check
        var mockReadEngineConfigSet = new List<EngineConfig>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<EngineConfig>()).Returns(mockReadEngineConfigSet.Object);

        var mockMediator = new Mock<IMediator>();
        mockMediator
            .Setup(m => m.Send(It.IsAny<EmergencyFlattenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<bool>.Init(true, true, "Flattened", "00"));

        var mockServiceProvider = new Mock<IServiceProvider>();
        mockServiceProvider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(mockReadContext.Object);
        mockServiceProvider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(mockWriteContext.Object);
        mockServiceProvider.Setup(p => p.GetService(typeof(IMediator))).Returns(mockMediator.Object);

        var mockScope = new Mock<IServiceScope>();
        mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        var options = new DailyPnlMonitorOptions
        {
            PollIntervalSeconds = 30,
            EmergencyFlattenEnabled = true
        };

        var worker = new DailyPnlMonitorWorker(
            mockScopeFactory.Object,
            options,
            Mock.Of<ILogger<DailyPnlMonitorWorker>>());

        return (worker, mockMediator);
    }

    [Fact]
    public async Task ExecuteAsync_NoBreach_NoFlattenDispatched()
    {
        // Account with 10000 equity, max daily loss 500, and start-of-day equity 10100
        // Daily loss = 10100 - 10000 = 100, which is below 500 threshold
        var account = EntityFactory.CreateAccount(equity: 10000m);
        account.MaxAbsoluteDailyLoss = 500m;

        var attribution = new AccountPerformanceAttribution
        {
            Id = 1,
            TradingAccountId = account.Id,
            AttributionDate = DateTime.UtcNow.Date,
            StartOfDayEquity = 10100m,
            IsDeleted = false
        };

        var (worker, mockMediator) = CreateWorkerWithScope(
            new List<TradingAccount> { account },
            new List<AccountPerformanceAttribution> { attribution },
            new List<DrawdownSnapshot>());

        // Use ExecuteAsync via reflection since it's protected
        // Instead, we start the worker with a cancellation that fires quickly
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await worker.StartAsync(cts.Token);
        await Task.Delay(300); // Give worker one cycle
        await worker.StopAsync(CancellationToken.None);

        mockMediator.Verify(
            m => m.Send(It.IsAny<EmergencyFlattenCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_BreachDetected_DispatchesEmergencyFlatten()
    {
        // Account with 8000 equity, max daily loss 500, start-of-day equity 10000
        // Daily loss = 10000 - 8000 = 2000, which exceeds 500 threshold
        var account = EntityFactory.CreateAccount(equity: 8000m);
        account.MaxAbsoluteDailyLoss = 500m;

        var attribution = new AccountPerformanceAttribution
        {
            Id = 1,
            TradingAccountId = account.Id,
            AttributionDate = DateTime.UtcNow.Date,
            StartOfDayEquity = 10000m,
            IsDeleted = false
        };

        var (worker, mockMediator) = CreateWorkerWithScope(
            new List<TradingAccount> { account },
            new List<AccountPerformanceAttribution> { attribution },
            new List<DrawdownSnapshot>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await worker.StartAsync(cts.Token);
        await Task.Delay(300); // Give worker one cycle
        await worker.StopAsync(CancellationToken.None);

        mockMediator.Verify(
            m => m.Send(It.IsAny<EmergencyFlattenCommand>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
}
