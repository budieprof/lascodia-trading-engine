using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.PaperTrading.Commands.SetPaperTradingMode;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.PaperTrading;

public class SetPaperTradingModeCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockContext;
    private readonly Mock<DbContext> _mockDbContext;

    public SetPaperTradingModeCommandTest()
    {
        _mockContext = new Mock<IWriteApplicationDbContext>();
        _mockDbContext = new Mock<DbContext>();
        _mockContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
        _mockContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
    }

    [Fact]
    public async Task Handle_NoExistingConfig_CreatesNew_PaperMode()
    {
        var configs = new List<EngineConfig>().AsQueryable().BuildMockDbSet();
        var logs = new List<DecisionLog>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EngineConfig>()).Returns(configs.Object);
        _mockDbContext.Setup(c => c.Set<DecisionLog>()).Returns(logs.Object);

        var handler = new SetPaperTradingModeCommandHandler(_mockContext.Object);

        var result = await handler.Handle(
            new SetPaperTradingModeCommand { IsPaperMode = true }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Contains("paper", result.data);
    }

    [Fact]
    public async Task Handle_ExistingConfig_UpdatesToLiveMode()
    {
        var existingConfig = new EngineConfig { Id = 1, Key = "Engine:PaperMode", Value = "true", IsDeleted = false };
        var configs = new List<EngineConfig> { existingConfig }.AsQueryable().BuildMockDbSet();
        var logs = new List<DecisionLog>().AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EngineConfig>()).Returns(configs.Object);
        _mockDbContext.Setup(c => c.Set<DecisionLog>()).Returns(logs.Object);

        var handler = new SetPaperTradingModeCommandHandler(_mockContext.Object);

        var result = await handler.Handle(
            new SetPaperTradingModeCommand { IsPaperMode = false, Reason = "Going live" }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Contains("live", result.data);
        Assert.Equal("false", existingConfig.Value);
    }
}
