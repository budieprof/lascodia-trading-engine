using System.Diagnostics.Metrics;
using System.Reflection;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class ReconciliationWorkerTest : IDisposable
{
    private readonly Mock<ILogger<ReconciliationWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<IMediator> _mockMediator;
    private readonly TradingMetrics _metrics;
    private readonly TestMeterFactory _meterFactory;
    private readonly ReconciliationWorker _worker;

    public ReconciliationWorkerTest()
    {
        _mockLogger       = new Mock<ILogger<ReconciliationWorker>>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockReadContext  = new Mock<IReadApplicationDbContext>();
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockMediator     = new Mock<IMediator>();
        _meterFactory     = new TestMeterFactory();
        _metrics          = new TradingMetrics(_meterFactory);

        var mockScope    = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();

        mockProvider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_mockReadContext.Object);
        mockProvider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_mockWriteContext.Object);
        mockProvider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mockMediator.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        _worker = new ReconciliationWorker(
            _mockScopeFactory.Object,
            _mockLogger.Object,
            _metrics);
    }

    public void Dispose() => _meterFactory.Dispose();

    private void SetupData(
        List<Position> positions,
        List<Order> orders,
        List<EAInstance> eaInstances,
        List<EngineConfig> configs)
    {
        var readMockDb = new Mock<DbContext>();
        var writeMockDb = new Mock<DbContext>();

        var posDbSet   = positions.AsQueryable().BuildMockDbSet();
        var orderDbSet = orders.AsQueryable().BuildMockDbSet();
        var eaDbSet    = eaInstances.AsQueryable().BuildMockDbSet();
        var cfgDbSet   = configs.AsQueryable().BuildMockDbSet();

        readMockDb.Setup(c => c.Set<Position>()).Returns(posDbSet.Object);
        readMockDb.Setup(c => c.Set<Order>()).Returns(orderDbSet.Object);
        readMockDb.Setup(c => c.Set<EAInstance>()).Returns(eaDbSet.Object);
        readMockDb.Setup(c => c.Set<EngineConfig>()).Returns(cfgDbSet.Object);

        writeMockDb.Setup(c => c.Set<Position>()).Returns(posDbSet.Object);
        writeMockDb.Setup(c => c.Set<Order>()).Returns(orderDbSet.Object);

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(readMockDb.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(writeMockDb.Object);
    }

    private async Task InvokeReconcileAsync(CancellationToken ct = default)
    {
        var method = typeof(ReconciliationWorker)
            .GetMethod("ReconcileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(_worker, new object[] { ct })!;
    }

    [Fact]
    public async Task Reconcile_NoOpenPositions_DoesNothing()
    {
        SetupData(
            positions: new List<Position>(),
            orders: new List<Order>(),
            eaInstances: new List<EAInstance>
            {
                new() { Id = 1, InstanceId = "EA-1", Status = EAInstanceStatus.Active, Symbols = "EURUSD" }
            },
            configs: new List<EngineConfig>());

        await InvokeReconcileAsync();

        _mockMediator.Verify(
            m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Reconcile_PositionCoveredByEA_NotClosed()
    {
        SetupData(
            positions: new List<Position>
            {
                new()
                {
                    Id = 1, Symbol = "EURUSD", Status = PositionStatus.Open,
                    BrokerPositionId = "12345", Direction = PositionDirection.Long, OpenLots = 1.0m,
                    AverageEntryPrice = 1.1000m
                }
            },
            orders: new List<Order>(),
            eaInstances: new List<EAInstance>
            {
                new() { Id = 1, InstanceId = "EA-1", Status = EAInstanceStatus.Active, Symbols = "EURUSD" }
            },
            configs: new List<EngineConfig>());

        await InvokeReconcileAsync();

        // No close decision should be logged — EA covers EURUSD
        _mockMediator.Verify(
            m => m.Send(It.Is<LogDecisionCommand>(c => c.DecisionType == "ReconciliationClosure"), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Reconcile_OrphanedPosition_NoEACoverage_AutoClosed()
    {
        var position = new Position
        {
            Id = 2, Symbol = "GBPUSD", Status = PositionStatus.Open,
            BrokerPositionId = "67890", Direction = PositionDirection.Short, OpenLots = 0.5m,
            AverageEntryPrice = 1.2500m
        };

        SetupData(
            positions: new List<Position> { position },
            orders: new List<Order>(),
            eaInstances: new List<EAInstance>
            {
                // EA only covers EURUSD, not GBPUSD
                new() { Id = 1, InstanceId = "EA-1", Status = EAInstanceStatus.Active, Symbols = "EURUSD" }
            },
            configs: new List<EngineConfig>());

        await InvokeReconcileAsync();

        // Position should be marked as closed
        Assert.Equal(PositionStatus.Closed, position.Status);
        Assert.NotNull(position.ClosedAt);

        // Audit trail should be written
        _mockMediator.Verify(
            m => m.Send(It.Is<LogDecisionCommand>(c =>
                c.DecisionType == "ReconciliationClosure" && c.EntityId == 2),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Reconcile_PositionWithNoBrokerTicket_SkippedNotClosed()
    {
        SetupData(
            positions: new List<Position>
            {
                new()
                {
                    Id = 3, Symbol = "USDJPY", Status = PositionStatus.Open,
                    BrokerPositionId = null, // No broker ticket
                    Direction = PositionDirection.Long, OpenLots = 1.0m,
                    AverageEntryPrice = 150.00m
                }
            },
            orders: new List<Order>(),
            eaInstances: new List<EAInstance>(),
            configs: new List<EngineConfig>());

        await InvokeReconcileAsync();

        // Should not attempt to close positions without broker ticket
        _mockMediator.Verify(
            m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
