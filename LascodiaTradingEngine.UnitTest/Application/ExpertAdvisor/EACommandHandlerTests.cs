using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.EventBus.Events;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ProcessHeartbeat;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.DeregisterEA;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveTickBatch;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveCandleBackfill;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.ExpertAdvisor;

public class EACommandHandlerTests
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<DbContext> _mockDbContext;

    public EACommandHandlerTests()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockDbContext = new Mock<DbContext>();
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
    }

    private static Mock<IEAOwnershipGuard> OwnerGuard(bool isOwner)
    {
        var mock = new Mock<IEAOwnershipGuard>();
        mock.Setup(g => g.IsOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(isOwner);
        return mock;
    }

    private void SetupEAInstances(List<EAInstance> instances)
    {
        var mockSet = instances.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EAInstance>()).Returns(mockSet.Object);
    }

    private static EAInstance CreateActiveInstance(string instanceId = "EA-001", long accountId = 1) => new()
    {
        Id = 1,
        InstanceId = instanceId,
        TradingAccountId = accountId,
        Symbols = "EURUSD",
        ChartSymbol = "EURUSD",
        ChartTimeframe = "H1",
        EAVersion = "1.0",
        Status = EAInstanceStatus.Active,
        LastHeartbeat = DateTime.UtcNow.AddMinutes(-1),
        IsDeleted = false
    };

    // ========================================================================
    //  ProcessHeartbeatCommand
    // ========================================================================

    [Fact]
    public async Task Heartbeat_ShouldSucceed_WithValidInstance()
    {
        var instance = CreateActiveInstance();
        SetupEAInstances([instance]);

        var handler = new ProcessHeartbeatCommandHandler(_mockWriteContext.Object, OwnerGuard(true).Object);

        var result = await handler.Handle(new ProcessHeartbeatCommand { InstanceId = "EA-001" }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.NotNull(result.data);
        _mockWriteContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Heartbeat_ShouldFail_WhenUnauthorized()
    {
        var instance = CreateActiveInstance();
        SetupEAInstances([instance]);

        var handler = new ProcessHeartbeatCommandHandler(_mockWriteContext.Object, OwnerGuard(false).Object);

        var result = await handler.Handle(new ProcessHeartbeatCommand { InstanceId = "EA-001" }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-403", result.responseCode);
        _mockWriteContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Heartbeat_ShouldFail_WhenInstanceNotFound()
    {
        SetupEAInstances([]);

        var handler = new ProcessHeartbeatCommandHandler(_mockWriteContext.Object, OwnerGuard(true).Object);

        var result = await handler.Handle(new ProcessHeartbeatCommand { InstanceId = "EA-999" }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    [Fact]
    public async Task Heartbeat_ShouldFail_WhenInstanceIsShuttingDown()
    {
        var instance = CreateActiveInstance();
        instance.Status = EAInstanceStatus.ShuttingDown;
        SetupEAInstances([instance]);

        var handler = new ProcessHeartbeatCommandHandler(_mockWriteContext.Object, OwnerGuard(true).Object);

        var result = await handler.Handle(new ProcessHeartbeatCommand { InstanceId = "EA-001" }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    // ========================================================================
    //  DeregisterEACommand
    // ========================================================================

    [Fact]
    public async Task Deregister_ShouldSucceed_WithActiveInstance()
    {
        var instance = CreateActiveInstance();
        SetupEAInstances([instance]);

        var mockEventBus = new Mock<IIntegrationEventService>();
        var handler = new DeregisterEACommandHandler(_mockWriteContext.Object, OwnerGuard(true).Object, mockEventBus.Object, Mock.Of<ILogger<DeregisterEACommandHandler>>());

        var result = await handler.Handle(new DeregisterEACommand { InstanceId = "EA-001" }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal(EAInstanceStatus.ShuttingDown, instance.Status);
        Assert.NotNull(instance.DeregisteredAt);
        mockEventBus.Verify(e => e.SaveAndPublish(
            It.IsAny<IDbContext>(),
            It.IsAny<IntegrationEvent>()), Times.Once);
    }

    [Fact]
    public async Task Deregister_ShouldFail_WhenUnauthorized()
    {
        var instance = CreateActiveInstance();
        SetupEAInstances([instance]);

        var handler = new DeregisterEACommandHandler(_mockWriteContext.Object, OwnerGuard(false).Object, new Mock<IIntegrationEventService>().Object, Mock.Of<ILogger<DeregisterEACommandHandler>>());

        var result = await handler.Handle(new DeregisterEACommand { InstanceId = "EA-001" }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-403", result.responseCode);
    }

    [Fact]
    public async Task Deregister_ShouldFail_WhenAlreadyShuttingDown()
    {
        var instance = CreateActiveInstance();
        instance.Status = EAInstanceStatus.ShuttingDown;
        SetupEAInstances([instance]);

        var handler = new DeregisterEACommandHandler(_mockWriteContext.Object, OwnerGuard(true).Object, new Mock<IIntegrationEventService>().Object, Mock.Of<ILogger<DeregisterEACommandHandler>>());

        var result = await handler.Handle(new DeregisterEACommand { InstanceId = "EA-001" }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    [Fact]
    public async Task Deregister_ShouldFail_WhenInstanceNotFound()
    {
        SetupEAInstances([]);

        var handler = new DeregisterEACommandHandler(_mockWriteContext.Object, OwnerGuard(true).Object, new Mock<IIntegrationEventService>().Object, Mock.Of<ILogger<DeregisterEACommandHandler>>());

        var result = await handler.Handle(new DeregisterEACommand { InstanceId = "EA-999" }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    // ========================================================================
    //  Validators
    // ========================================================================

    [Fact]
    public void HeartbeatValidator_ShouldFail_WhenInstanceIdEmpty()
    {
        var validator = new ProcessHeartbeatCommandValidator();
        var result = validator.TestValidate(new ProcessHeartbeatCommand { InstanceId = "" });
        Assert.False(result.IsValid);
    }

    [Fact]
    public void HeartbeatValidator_ShouldPass_WithValidCommand()
    {
        var validator = new ProcessHeartbeatCommandValidator();
        var result = validator.TestValidate(new ProcessHeartbeatCommand { InstanceId = "EA-001" });
        Assert.True(result.IsValid);
    }

    [Fact]
    public void DeregisterValidator_ShouldFail_WhenInstanceIdEmpty()
    {
        var validator = new DeregisterEACommandValidator();
        var result = validator.TestValidate(new DeregisterEACommand { InstanceId = "" });
        Assert.False(result.IsValid);
    }
}
