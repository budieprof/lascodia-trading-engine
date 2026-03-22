using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.EconomicEvents.Commands.CreateEconomicEvent;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.EconomicEvents;

public class CreateEconomicEventCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly CreateEconomicEventCommandHandler _handler;
    private readonly CreateEconomicEventCommandValidator _validator;

    public CreateEconomicEventCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();

        var mockDbContext = new Mock<DbContext>();
        var events = new List<EconomicEvent>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<EconomicEvent>()).Returns(events.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler = new CreateEconomicEventCommandHandler(_mockWriteContext.Object);
        _validator = new CreateEconomicEventCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Title_Is_Empty()
    {
        var command = new CreateEconomicEventCommand
        {
            Title = string.Empty,
            Currency = "USD",
            Impact = "High",
            ScheduledAt = DateTime.UtcNow.AddDays(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Title)
              .WithErrorMessage("Title is required");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Currency_Is_Empty()
    {
        var command = new CreateEconomicEventCommand
        {
            Title = "Non-Farm Payrolls",
            Currency = string.Empty,
            Impact = "High",
            ScheduledAt = DateTime.UtcNow.AddDays(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Currency)
              .WithErrorMessage("Currency is required");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Currency_Exceeds_3_Characters()
    {
        var command = new CreateEconomicEventCommand
        {
            Title = "Non-Farm Payrolls",
            Currency = "USDX",
            Impact = "High",
            ScheduledAt = DateTime.UtcNow.AddDays(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Currency)
              .WithErrorMessage("Currency cannot exceed 3 characters");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Impact_Is_Invalid()
    {
        var command = new CreateEconomicEventCommand
        {
            Title = "Non-Farm Payrolls",
            Currency = "USD",
            Impact = "Extreme",
            ScheduledAt = DateTime.UtcNow.AddDays(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Impact)
              .WithErrorMessage("Impact must be 'High', 'Medium', or 'Low'");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new CreateEconomicEventCommand
        {
            Title = "Non-Farm Payrolls",
            Currency = "USD",
            Impact = "High",
            ScheduledAt = DateTime.UtcNow.AddDays(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success()
    {
        var command = new CreateEconomicEventCommand
        {
            Title = "Non-Farm Payrolls",
            Currency = "USD",
            Impact = "High",
            ScheduledAt = DateTime.UtcNow.AddDays(1),
            Source = "Manual"
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
    }
}
