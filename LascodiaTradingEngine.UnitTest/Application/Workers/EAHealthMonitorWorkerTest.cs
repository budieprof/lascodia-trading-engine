using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class EAHealthMonitorWorkerTest : IDisposable
{
    private readonly Mock<ILogger<EAHealthMonitorWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<IIntegrationEventService> _mockEventBus;
    private readonly TradingMetrics _metrics;
    private readonly TestMeterFactory _meterFactory;
    private readonly EAHealthMonitorWorker _worker;

    public EAHealthMonitorWorkerTest()
    {
        _mockLogger       = new Mock<ILogger<EAHealthMonitorWorker>>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockEventBus     = new Mock<IIntegrationEventService>();
        _meterFactory     = new TestMeterFactory();
        _metrics          = new TradingMetrics(_meterFactory);

        var mockScope    = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();

        mockProvider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_mockWriteContext.Object);
        mockProvider.Setup(p => p.GetService(typeof(IIntegrationEventService))).Returns(_mockEventBus.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        _worker = new EAHealthMonitorWorker(
            _mockScopeFactory.Object,
            _mockLogger.Object,
            _metrics);
    }

    public void Dispose() => _meterFactory.Dispose();

    private void SetupEAInstances(List<EAInstance> instances)
    {
        var mockDbContext = new Mock<DbContext>();
        var dbSet = instances.AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<EAInstance>()).Returns(dbSet.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
    }

    private async Task InvokeCheckHeartbeatsAsync(CancellationToken ct = default)
    {
        var method = typeof(EAHealthMonitorWorker)
            .GetMethod("CheckHeartbeatsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(_worker, new object[] { ct })!;
    }

    [Fact]
    public async Task CheckHeartbeats_AllFresh_NoStatusChanges()
    {
        var instances = new List<EAInstance>
        {
            new()
            {
                Id = 1, InstanceId = "EA-1", Status = EAInstanceStatus.Active,
                Symbols = "EURUSD", LastHeartbeat = DateTime.UtcNow.AddSeconds(-10)
            }
        };
        SetupEAInstances(instances);

        await InvokeCheckHeartbeatsAsync();

        Assert.Equal(EAInstanceStatus.Active, instances[0].Status);
    }

    [Fact]
    public async Task CheckHeartbeats_StaleInstance_MarkedDisconnected()
    {
        var instances = new List<EAInstance>
        {
            new()
            {
                Id = 2, InstanceId = "EA-STALE", Status = EAInstanceStatus.Active,
                Symbols = "GBPUSD", LastHeartbeat = DateTime.UtcNow.AddSeconds(-120)
            }
        };
        SetupEAInstances(instances);

        await InvokeCheckHeartbeatsAsync();

        Assert.Equal(EAInstanceStatus.Disconnected, instances[0].Status);
        _mockEventBus.Verify(e => e.SaveAndPublish(
            It.IsAny<Lascodia.Trading.Engine.SharedApplication.Common.Interfaces.IDbContext>(),
            It.IsAny<Lascodia.Trading.Engine.EventBus.Events.IntegrationEvent>()), Times.Once);
    }

    [Fact]
    public async Task CheckHeartbeats_MixedInstances_OnlyStaleMarked()
    {
        var instances = new List<EAInstance>
        {
            new()
            {
                Id = 3, InstanceId = "EA-FRESH", Status = EAInstanceStatus.Active,
                Symbols = "EURUSD", LastHeartbeat = DateTime.UtcNow.AddSeconds(-5)
            },
            new()
            {
                Id = 4, InstanceId = "EA-STALE", Status = EAInstanceStatus.Active,
                Symbols = "GBPUSD", LastHeartbeat = DateTime.UtcNow.AddSeconds(-90)
            }
        };
        SetupEAInstances(instances);

        await InvokeCheckHeartbeatsAsync();

        Assert.Equal(EAInstanceStatus.Active, instances[0].Status);
        Assert.Equal(EAInstanceStatus.Disconnected, instances[1].Status);
    }

    [Fact]
    public async Task CheckHeartbeats_AlreadyDisconnected_NotReprocessed()
    {
        var instances = new List<EAInstance>
        {
            new()
            {
                Id = 5, InstanceId = "EA-DISC", Status = EAInstanceStatus.Disconnected,
                Symbols = "USDJPY", LastHeartbeat = DateTime.UtcNow.AddSeconds(-300)
            }
        };
        SetupEAInstances(instances);

        await InvokeCheckHeartbeatsAsync();

        // Should still be Disconnected, and SaveChanges should NOT be called (no changes)
        Assert.Equal(EAInstanceStatus.Disconnected, instances[0].Status);
        _mockWriteContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
