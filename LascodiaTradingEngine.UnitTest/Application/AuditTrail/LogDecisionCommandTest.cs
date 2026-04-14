using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.AuditTrail;

public class LogDecisionCommandTest
{
    private readonly LogDecisionCommandValidator _validator = new();

    [Fact]
    public async Task Validator_Fails_When_EntityType_Empty()
    {
        var cmd = new LogDecisionCommand { EntityType = "", EntityId = 1, DecisionType = "Test", Outcome = "Ok", Reason = "r", Source = "s" };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldHaveValidationErrorFor(c => c.EntityType);
    }

    [Fact]
    public async Task Validator_Allows_EntityId_Zero_For_Pipeline_Level_Events()
    {
        var cmd = new LogDecisionCommand { EntityType = "Strategy", EntityId = 0, DecisionType = "Test", Outcome = "Ok", Reason = "r", Source = "s" };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldNotHaveValidationErrorFor(c => c.EntityId);
    }

    [Fact]
    public async Task Validator_Fails_When_EntityId_Negative()
    {
        var cmd = new LogDecisionCommand { EntityType = "Order", EntityId = -1, DecisionType = "Test", Outcome = "Ok", Reason = "r", Source = "s" };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldHaveValidationErrorFor(c => c.EntityId);
    }

    [Fact]
    public async Task Validator_Fails_When_DecisionType_TooLong()
    {
        var cmd = new LogDecisionCommand { EntityType = "Order", EntityId = 1, DecisionType = new string('x', 51), Outcome = "Ok", Reason = "r", Source = "s" };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldHaveValidationErrorFor(c => c.DecisionType);
    }

    [Fact]
    public async Task Validator_Passes_Valid_Command()
    {
        var cmd = new LogDecisionCommand { EntityType = "Order", EntityId = 1, DecisionType = "Test", Outcome = "Ok", Reason = "reason", Source = "Worker" };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Creates_DecisionLog_Successfully()
    {
        var mockContext = new Mock<IWriteApplicationDbContext>();
        var mockDbContext = new Mock<DbContext>();
        var logs = new List<DecisionLog>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<DecisionLog>()).Returns(logs.Object);
        mockContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
        mockContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var handler = new LogDecisionCommandHandler(mockContext.Object);
        var cmd = new LogDecisionCommand { EntityType = "Order", EntityId = 1, DecisionType = "Test", Outcome = "Ok", Reason = "r", Source = "s" };

        var result = await handler.Handle(cmd, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
    }
}
