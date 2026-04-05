using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyFeedback.Commands.RejectOptimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyFeedback.Commands;

public class RejectOptimizationCommandTest
{
    [Fact]
    public async Task Handle_MarksRunRejectedAndCompleted()
    {
        var runs = new List<OptimizationRun>
        {
            new()
            {
                Id = 81,
                StrategyId = 12,
                Status = OptimizationRunStatus.Completed,
                CompletedAt = null,
                ErrorMessage = "Old error",
                FailureCategory = OptimizationFailureCategory.Transient,
                ExecutionLeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                ExecutionLeaseToken = Guid.NewGuid(),
                IsDeleted = false
            }
        };

        var runDbSet = runs.AsQueryable().BuildMockDbSet();
        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(runDbSet.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var handler = new RejectOptimizationCommandHandler(writeCtx.Object);

        var response = await handler.Handle(new RejectOptimizationCommand { Id = 81 }, CancellationToken.None);

        Assert.True(response.status);
        Assert.Equal(OptimizationRunStatus.Rejected, runs[0].Status);
        Assert.NotNull(runs[0].CompletedAt);
        Assert.Null(runs[0].ErrorMessage);
        Assert.Null(runs[0].FailureCategory);
        Assert.Null(runs[0].ExecutionLeaseExpiresAt);
        Assert.Null(runs[0].ExecutionLeaseToken);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ReturnsNotFound_WhenRunMissing()
    {
        var runs = new List<OptimizationRun>();
        var runDbSet = runs.AsQueryable().BuildMockDbSet();
        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(runDbSet.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var handler = new RejectOptimizationCommandHandler(writeCtx.Object);

        var response = await handler.Handle(new RejectOptimizationCommand { Id = 999 }, CancellationToken.None);

        Assert.False(response.status);
        Assert.Contains("not found", response.message!, StringComparison.OrdinalIgnoreCase);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Validator_RejectsNonPositiveId()
    {
        var validator = new RejectOptimizationCommandValidator();
        var result = validator.TestValidate(new RejectOptimizationCommand { Id = 0 });

        result.ShouldHaveValidationErrorFor(x => x.Id);
    }
}
