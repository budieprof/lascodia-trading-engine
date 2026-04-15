using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Strategies.Commands.PromotePendingModelStrategy;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Strategies.Commands;

public class PromotePendingModelStrategyCommandHandlerTests
{
    private static (PromotePendingModelStrategyCommandHandler handler,
                    Mock<IWriteApplicationDbContext> write,
                    List<Strategy> strategyStore)
        BuildHandler(Strategy? existingStrategy = null, MLModel? activatedModel = null)
    {
        var strategyStore = existingStrategy is null ? new List<Strategy>() : new List<Strategy> { existingStrategy };
        var modelStore = activatedModel is null ? new List<MLModel>() : new List<MLModel> { activatedModel };

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        var writeDb = new Mock<DbContext>();
        var strategiesSet = strategyStore.AsQueryable().BuildMockDbSet();
        writeDb.Setup(c => c.Set<Strategy>()).Returns(strategiesSet.Object);
        writeCtx.Setup(c => c.GetDbContext()).Returns(writeDb.Object);
        writeCtx.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var readCtx = new Mock<IReadApplicationDbContext>();
        var readDb = new Mock<DbContext>();
        var modelsSet = modelStore.AsQueryable().BuildMockDbSet();
        readDb.Setup(c => c.Set<MLModel>()).Returns(modelsSet.Object);
        readCtx.Setup(c => c.GetDbContext()).Returns(readDb.Object);

        var handler = new PromotePendingModelStrategyCommandHandler(
            writeCtx.Object,
            readCtx.Object,
            TimeProvider.System,
            NullLogger<PromotePendingModelStrategyCommandHandler>.Instance);

        return (handler, writeCtx, strategyStore);
    }

    [Fact]
    public async Task Returns_NotFound_When_Strategy_Missing()
    {
        var (handler, _, _) = BuildHandler();
        var result = await handler.Handle(new PromotePendingModelStrategyCommand
        {
            StrategyId = 42,
            ActivatedMLModelId = 1,
        }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-14", result.responseCode);
    }

    [Fact]
    public async Task NoOps_When_Strategy_Already_Out_Of_PendingModel_Stage()
    {
        var strategy = new Strategy
        {
            Id = 10,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            StrategyType = StrategyType.CompositeML,
            LifecycleStage = StrategyLifecycleStage.Active,
            Status = StrategyStatus.Active,
        };
        var model = new MLModel
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            IsActive = true,
        };
        var (handler, _, _) = BuildHandler(strategy, model);

        var result = await handler.Handle(new PromotePendingModelStrategyCommand
        {
            StrategyId = 10,
            ActivatedMLModelId = 1,
        }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("Already promoted", result.message);
        // Stage should not have been flipped back.
        Assert.Equal(StrategyLifecycleStage.Active, strategy.LifecycleStage);
    }

    [Fact]
    public async Task Refuses_Promotion_On_Model_Combo_Mismatch()
    {
        var strategy = new Strategy
        {
            Id = 10,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            StrategyType = StrategyType.CompositeML,
            LifecycleStage = StrategyLifecycleStage.PendingModel,
            Status = StrategyStatus.Paused,
        };
        var wrongModel = new MLModel
        {
            Id = 1,
            Symbol = "GBPUSD",       // different symbol
            Timeframe = Timeframe.H1,
            IsActive = true,
        };
        var (handler, _, _) = BuildHandler(strategy, wrongModel);

        var result = await handler.Handle(new PromotePendingModelStrategyCommand
        {
            StrategyId = 10,
            ActivatedMLModelId = 1,
        }, CancellationToken.None);

        Assert.False(result.status);
        Assert.Equal("-11", result.responseCode);
        Assert.Equal(StrategyLifecycleStage.PendingModel, strategy.LifecycleStage);
    }

    [Fact]
    public async Task Promotes_PendingModel_Strategy_To_Draft_On_Matching_Model()
    {
        var strategy = new Strategy
        {
            Id = 10,
            Symbol = "USDJPY",
            Timeframe = Timeframe.M5,
            StrategyType = StrategyType.CompositeML,
            LifecycleStage = StrategyLifecycleStage.PendingModel,
            LifecycleStageEnteredAt = DateTime.UtcNow.AddDays(-1),
            Status = StrategyStatus.Paused,
        };
        var model = new MLModel
        {
            Id = 77,
            Symbol = "USDJPY",
            Timeframe = Timeframe.M5,
            IsActive = true,
        };
        var (handler, writeCtx, _) = BuildHandler(strategy, model);

        var priorEnteredAt = strategy.LifecycleStageEnteredAt;
        var result = await handler.Handle(new PromotePendingModelStrategyCommand
        {
            StrategyId = 10,
            ActivatedMLModelId = 77,
        }, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal(StrategyLifecycleStage.Draft, strategy.LifecycleStage);
        Assert.NotNull(strategy.LifecycleStageEnteredAt);
        Assert.NotEqual(priorEnteredAt, strategy.LifecycleStageEnteredAt);
        Assert.Contains("Promoted from PendingModel", strategy.PauseReason ?? "");
        writeCtx.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
