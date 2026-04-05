using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyFeedback.Commands.TriggerOptimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyFeedback.Commands;

public class TriggerOptimizationCommandTest
{
    [Fact]
    public async Task Handle_ReturnsExistingActiveRun_WhenOneAlreadyExists()
    {
        var runs = new List<OptimizationRun>
        {
            new()
            {
                Id = 91,
                StrategyId = 12,
                Status = OptimizationRunStatus.Queued,
                StartedAt = DateTime.UtcNow.AddMinutes(-3),
                IsDeleted = false
            }
        };

        var runDbSet = runs.AsQueryable().BuildMockDbSet();
        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(runDbSet.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var handler = new TriggerOptimizationCommandHandler(writeCtx.Object);

        var response = await handler.Handle(new TriggerOptimizationCommand
        {
            StrategyId = 12,
            TriggerType = "Manual"
        }, CancellationToken.None);

        Assert.True(response.status);
        Assert.Equal(91, response.data);
        Assert.Contains("already queued or running", response.message!, StringComparison.OrdinalIgnoreCase);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_QueuesRun_WhenNoActiveRunExists()
    {
        var runs = new List<OptimizationRun>();
        var runDbSet = runs.AsQueryable().BuildMockDbSet();
        runDbSet.Setup(d => d.AddAsync(It.IsAny<OptimizationRun>(), It.IsAny<CancellationToken>()))
            .Callback<OptimizationRun, CancellationToken>((run, _) =>
            {
                run.Id = 123;
                runs.Add(run);
            });

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(runDbSet.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var handler = new TriggerOptimizationCommandHandler(writeCtx.Object);

        var response = await handler.Handle(new TriggerOptimizationCommand
        {
            StrategyId = 17,
            TriggerType = "Manual"
        }, CancellationToken.None);

        Assert.True(response.status);
        Assert.Equal(123, response.data);
        Assert.Single(runs);
        Assert.Equal(OptimizationRunStatus.Queued, runs[0].Status);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Validator_RejectsNonPositiveStrategyId()
    {
        var validator = new TriggerOptimizationCommandValidator();
        var result = validator.TestValidate(new TriggerOptimizationCommand
        {
            StrategyId = 0,
            TriggerType = "Manual"
        });

        result.ShouldHaveValidationErrorFor(x => x.StrategyId);
    }
}
