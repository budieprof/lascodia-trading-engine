using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.ExecutionQuality.Commands.RecordExecutionQuality;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.ExecutionQuality;

public class RecordExecutionQualityCommandTest
{
    private readonly RecordExecutionQualityCommandValidator _validator = new();

    [Fact]
    public async Task Validator_Fails_When_OrderId_Zero()
    {
        var cmd = new RecordExecutionQualityCommand { OrderId = 0, Symbol = "EURUSD", Session = "London" };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldHaveValidationErrorFor(c => c.OrderId);
    }

    [Fact]
    public async Task Validator_Fails_When_Symbol_Empty()
    {
        var cmd = new RecordExecutionQualityCommand { OrderId = 1, Symbol = "", Session = "London" };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldHaveValidationErrorFor(c => c.Symbol);
    }

    [Fact]
    public async Task Validator_Fails_When_Session_Invalid()
    {
        var cmd = new RecordExecutionQualityCommand { OrderId = 1, Symbol = "EURUSD", Session = "Weekend" };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldHaveValidationErrorFor(c => c.Session);
    }

    [Fact]
    public async Task Validator_Passes_Valid_Command()
    {
        var cmd = new RecordExecutionQualityCommand { OrderId = 1, Symbol = "EURUSD", Session = "London" };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Creates_Log_Successfully()
    {
        var mockContext = new Mock<IWriteApplicationDbContext>();
        var mockDbContext = new Mock<DbContext>();
        var logs = new List<ExecutionQualityLog>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<ExecutionQualityLog>()).Returns(logs.Object);
        mockContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
        mockContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var handler = new RecordExecutionQualityCommandHandler(mockContext.Object);
        var cmd = new RecordExecutionQualityCommand
        {
            OrderId = 1, Symbol = "EURUSD", Session = "London",
            RequestedPrice = 1.1000m, FilledPrice = 1.1002m, SlippagePips = 0.2m,
            SubmitToFillMs = 45, FillRate = 1.0m
        };

        var result = await handler.Handle(cmd, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
    }
}
