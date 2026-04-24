using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Sentiment.Commands.RecordSentiment;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Sentiment;

public class RecordSentimentCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<IIntegrationEventService> _mockEventBus;
    private readonly RecordSentimentCommandHandler _handler;
    private readonly RecordSentimentCommandValidator _validator;

    public RecordSentimentCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockEventBus = new Mock<IIntegrationEventService>();

        var mockDbContext = new Mock<DbContext>();
        var snapshots = new List<SentimentSnapshot>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<SentimentSnapshot>()).Returns(snapshots.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
        _mockEventBus
            .Setup(bus => bus.SaveAndPublish(_mockWriteContext.Object, It.IsAny<Lascodia.Trading.Engine.EventBus.Events.IntegrationEvent>()))
            .Returns(Task.CompletedTask);

        _handler = new RecordSentimentCommandHandler(_mockWriteContext.Object, _mockEventBus.Object);
        _validator = new RecordSentimentCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Symbol_Is_Empty()
    {
        var command = new RecordSentimentCommand
        {
            Symbol = string.Empty,
            Source = "COT",
            SentimentScore = 0.5m,
            BullishPct = 60m,
            BearishPct = 30m,
            NeutralPct = 10m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Symbol)
              .WithErrorMessage("Symbol is required");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Source_Is_Empty()
    {
        var command = new RecordSentimentCommand
        {
            Symbol = "EURUSD",
            Source = string.Empty,
            SentimentScore = 0.5m,
            BullishPct = 60m,
            BearishPct = 30m,
            NeutralPct = 10m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Source)
              .WithErrorMessage("Source is required");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_SentimentScore_Exceeds_1()
    {
        var command = new RecordSentimentCommand
        {
            Symbol = "EURUSD",
            Source = "COT",
            SentimentScore = 1.5m,
            BullishPct = 60m,
            BearishPct = 30m,
            NeutralPct = 10m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.SentimentScore)
              .WithErrorMessage("SentimentScore must be between -1 and 1");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_SentimentScore_Below_Negative_1()
    {
        var command = new RecordSentimentCommand
        {
            Symbol = "EURUSD",
            Source = "COT",
            SentimentScore = -1.5m,
            BullishPct = 60m,
            BearishPct = 30m,
            NeutralPct = 10m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.SentimentScore)
              .WithErrorMessage("SentimentScore must be between -1 and 1");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new RecordSentimentCommand
        {
            Symbol = "EURUSD",
            Source = "NewsSentiment",
            SentimentScore = 0.75m,
            BullishPct = 60m,
            BearishPct = 30m,
            NeutralPct = 10m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Boundary_SentimentScore_Values()
    {
        var commandMin = new RecordSentimentCommand
        {
            Symbol = "EURUSD",
            Source = "COT",
            SentimentScore = -1m,
            BullishPct = 10m,
            BearishPct = 80m,
            NeutralPct = 10m
        };

        var resultMin = await _validator.TestValidateAsync(commandMin);
        resultMin.ShouldNotHaveAnyValidationErrors();

        var commandMax = new RecordSentimentCommand
        {
            Symbol = "EURUSD",
            Source = "COT",
            SentimentScore = 1m,
            BullishPct = 80m,
            BearishPct = 10m,
            NeutralPct = 10m
        };

        var resultMax = await _validator.TestValidateAsync(commandMax);
        resultMax.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success()
    {
        var command = new RecordSentimentCommand
        {
            Symbol = "EURUSD",
            Source = "COT",
            SentimentScore = 0.5m,
            BullishPct = 60m,
            BearishPct = 30m,
            NeutralPct = 10m
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
    }
}
