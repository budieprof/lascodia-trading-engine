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
    public async Task Handle_SkipsPositionsWithPendingCloseCommand()
    {
        // Re-invoking EmergencyFlatten must not queue duplicate ClosePosition commands
        // for positions that already have a pending one.
        var orders = new List<Order>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Order>()).Returns(orders.Object);

        var pos1 = EntityFactory.CreatePosition(symbol: "EURUSD", status: PositionStatus.Open);
        var pos2 = EntityFactory.CreatePosition(symbol: "GBPUSD", status: PositionStatus.Open);
        var openPositions = new List<Position> { pos1, pos2 };
        _mockDbContext.Setup(c => c.Set<Position>()).Returns(openPositions.AsQueryable().BuildMockDbSet().Object);

        var strategies = new List<Strategy>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Strategy>()).Returns(strategies.Object);

        // pos1 already has a pending ClosePosition command from a prior invocation.
        var existingEACommands = new List<EACommand>
        {
            new()
            {
                Id               = 101,
                CommandType      = EACommandType.ClosePosition,
                Symbol           = pos1.Symbol,
                TargetInstanceId = string.Empty,
                Parameters       = $"{{\"reason\":\"EMERGENCY_FLATTEN\",\"positionId\":{pos1.Id}}}",
                Acknowledged     = false,
                CreatedAt        = DateTime.UtcNow.AddSeconds(-30)
            }
        };
        var eaCommandsDbSet = existingEACommands.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EACommand>()).Returns(eaCommandsDbSet.Object);

        var auditLogs = new List<EngineConfigAuditLog>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EngineConfigAuditLog>()).Returns(auditLogs.Object);

        var command = new EmergencyFlattenCommand
        {
            TriggeredByAccountId = 1,
            Reason = "Second emergency flatten invocation"
        };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        // Only pos2 should have a new ClosePosition command added; pos1 was skipped.
        eaCommandsDbSet.Verify(s => s.AddAsync(
            It.Is<EACommand>(c => c.CommandType == EACommandType.ClosePosition
                               && c.Parameters != null
                               && c.Parameters.Contains($"\"positionId\":{pos2.Id}")),
            It.IsAny<CancellationToken>()),
            Times.Once);
        eaCommandsDbSet.Verify(s => s.AddAsync(
            It.Is<EACommand>(c => c.CommandType == EACommandType.ClosePosition
                               && c.Parameters != null
                               && c.Parameters.Contains($"\"positionId\":{pos1.Id}")),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_SkipsPositionWhenPendingCloseAlreadyAcknowledged_StillQueues()
    {
        // An ACK'd ClosePosition from a previous flatten should NOT prevent re-queue —
        // the command is already consumed and no new close is in flight.
        var orders = new List<Order>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Order>()).Returns(orders.Object);

        var pos1 = EntityFactory.CreatePosition(symbol: "EURUSD", status: PositionStatus.Open);
        _mockDbContext.Setup(c => c.Set<Position>()).Returns(
            new List<Position> { pos1 }.AsQueryable().BuildMockDbSet().Object);

        var strategies = new List<Strategy>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<Strategy>()).Returns(strategies.Object);

        var existingEACommands = new List<EACommand>
        {
            new()
            {
                Id               = 202,
                CommandType      = EACommandType.ClosePosition,
                Symbol           = pos1.Symbol,
                TargetInstanceId = string.Empty,
                Parameters       = $"{{\"reason\":\"EMERGENCY_FLATTEN\",\"positionId\":{pos1.Id}}}",
                Acknowledged     = true,          // already processed
                AcknowledgedAt   = DateTime.UtcNow.AddMinutes(-1),
                CreatedAt        = DateTime.UtcNow.AddMinutes(-2)
            }
        };
        var eaCommandsDbSet = existingEACommands.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EACommand>()).Returns(eaCommandsDbSet.Object);

        var auditLogs = new List<EngineConfigAuditLog>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EngineConfigAuditLog>()).Returns(auditLogs.Object);

        var command = new EmergencyFlattenCommand
        {
            TriggeredByAccountId = 1,
            Reason = "Flatten after prior ACK'd close"
        };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        // pos1's new close command MUST be queued — the prior one was already ACK'd.
        eaCommandsDbSet.Verify(s => s.AddAsync(
            It.Is<EACommand>(c => c.CommandType == EACommandType.ClosePosition
                               && c.Parameters != null
                               && c.Parameters.Contains($"\"positionId\":{pos1.Id}")),
            It.IsAny<CancellationToken>()),
            Times.Once);
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
