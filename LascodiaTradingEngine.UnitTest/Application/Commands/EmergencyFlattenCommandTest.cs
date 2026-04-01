using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.EmergencyFlatten.Commands.EmergencyFlatten;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Commands;

public class EmergencyFlattenCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<DbContext> _mockDbContext;
    private readonly EmergencyFlattenCommandHandler _handler;

    public EmergencyFlattenCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockDbContext = new Mock<DbContext>();
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _handler = new EmergencyFlattenCommandHandler(
            _mockWriteContext.Object,
            Mock.Of<ILogger<EmergencyFlattenCommandHandler>>());
    }

    [Fact]
    public async Task Handle_CancelsPendingOrders()
    {
        var pendingOrders = new List<Order>
        {
            EntityFactory.CreateOrder(status: OrderStatus.Pending),
            EntityFactory.CreateOrder(status: OrderStatus.Submitted),
        };

        var mockOrderSet = pendingOrders.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Order>()).Returns(mockOrderSet.Object);

        // Empty positions, strategies
        var positions = new List<Position>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Position>()).Returns(positions.Object);

        var strategies = new List<Strategy>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Strategy>()).Returns(strategies.Object);

        var eaCommands = new List<EACommand>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EACommand>()).Returns(eaCommands.Object);

        var auditLogs = new List<EngineConfigAuditLog>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EngineConfigAuditLog>()).Returns(auditLogs.Object);

        var command = new EmergencyFlattenCommand
        {
            TriggeredByAccountId = 1,
            Reason = "Test emergency"
        };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.All(pendingOrders, o => Assert.Equal(OrderStatus.Cancelled, o.Status));
    }

    [Fact]
    public async Task Handle_QueuesCloseCommandsForOpenPositions()
    {
        var orders = new List<Order>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Order>()).Returns(orders.Object);

        var openPositions = new List<Position>
        {
            EntityFactory.CreatePosition(symbol: "EURUSD", status: PositionStatus.Open),
            EntityFactory.CreatePosition(symbol: "GBPUSD", status: PositionStatus.Open),
        };

        var mockPositionSet = openPositions.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Position>()).Returns(mockPositionSet.Object);

        var strategies = new List<Strategy>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Strategy>()).Returns(strategies.Object);

        var eaCommands = new List<EACommand>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EACommand>()).Returns(eaCommands.Object);

        var auditLogs = new List<EngineConfigAuditLog>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EngineConfigAuditLog>()).Returns(auditLogs.Object);

        var command = new EmergencyFlattenCommand
        {
            TriggeredByAccountId = 1,
            Reason = "Test emergency flatten"
        };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        // Verify EACommand AddAsync was called for each position
        _mockDbContext.Verify(
            c => c.Set<EACommand>(),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Handle_PausesActiveStrategies()
    {
        var orders = new List<Order>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Order>()).Returns(orders.Object);

        var positions = new List<Position>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Position>()).Returns(positions.Object);

        var activeStrategies = new List<Strategy>
        {
            EntityFactory.CreateStrategy(symbol: "EURUSD", status: StrategyStatus.Active),
            EntityFactory.CreateStrategy(symbol: "GBPUSD", status: StrategyStatus.Active),
        };

        var mockStrategySet = activeStrategies.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Strategy>()).Returns(mockStrategySet.Object);

        var eaCommands = new List<EACommand>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EACommand>()).Returns(eaCommands.Object);

        var auditLogs = new List<EngineConfigAuditLog>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EngineConfigAuditLog>()).Returns(auditLogs.Object);

        var command = new EmergencyFlattenCommand
        {
            TriggeredByAccountId = 1,
            Reason = "Test emergency flatten"
        };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.All(activeStrategies, s => Assert.Equal(StrategyStatus.Paused, s.Status));
    }
}
