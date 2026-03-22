using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradeSignals.Commands.CreateTradeSignal;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.TradeSignals;

public class CreateTradeSignalCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<IIntegrationEventService> _mockEventService;
    private readonly CreateTradeSignalCommandHandler _handler;
    private readonly CreateTradeSignalCommandValidator _validator;

    public CreateTradeSignalCommandTest()
    {
        _mockWriteContext  = new Mock<IWriteApplicationDbContext>();
        _mockEventService  = new Mock<IIntegrationEventService>();

        var mockDbContext = new Mock<DbContext>();
        var signals = new List<TradeSignal>().AsQueryable().BuildMockDbSet();
        var predLogs = new List<MLModelPredictionLog>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<TradeSignal>()).Returns(signals.Object);
        mockDbContext.Setup(c => c.Set<MLModelPredictionLog>()).Returns(predLogs.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler   = new CreateTradeSignalCommandHandler(_mockWriteContext.Object, _mockEventService.Object);
        _validator = new CreateTradeSignalCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_StrategyId_Is_Zero()
    {
        var command = new CreateTradeSignalCommand
        {
            StrategyId       = 0,
            Symbol           = "EURUSD",
            Direction        = "Buy",
            EntryPrice       = 1.1000m,
            SuggestedLotSize = 0.01m,
            Confidence       = 0.8m,
            ExpiresAt        = DateTime.UtcNow.AddHours(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.StrategyId)
              .WithErrorMessage("StrategyId must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Symbol_Is_Empty()
    {
        var command = new CreateTradeSignalCommand
        {
            StrategyId       = 1,
            Symbol           = string.Empty,
            Direction        = "Buy",
            EntryPrice       = 1.1000m,
            SuggestedLotSize = 0.01m,
            Confidence       = 0.8m,
            ExpiresAt        = DateTime.UtcNow.AddHours(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Symbol)
              .WithErrorMessage("Symbol cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Direction_Is_Invalid()
    {
        var command = new CreateTradeSignalCommand
        {
            StrategyId       = 1,
            Symbol           = "EURUSD",
            Direction        = "Hold",
            EntryPrice       = 1.1000m,
            SuggestedLotSize = 0.01m,
            Confidence       = 0.8m,
            ExpiresAt        = DateTime.UtcNow.AddHours(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Direction)
              .WithErrorMessage("Direction must be 'Buy' or 'Sell'");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_EntryPrice_Is_Zero()
    {
        var command = new CreateTradeSignalCommand
        {
            StrategyId       = 1,
            Symbol           = "EURUSD",
            Direction        = "Buy",
            EntryPrice       = 0m,
            SuggestedLotSize = 0.01m,
            Confidence       = 0.8m,
            ExpiresAt        = DateTime.UtcNow.AddHours(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.EntryPrice)
              .WithErrorMessage("EntryPrice must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_SuggestedLotSize_Is_Zero()
    {
        var command = new CreateTradeSignalCommand
        {
            StrategyId       = 1,
            Symbol           = "EURUSD",
            Direction        = "Buy",
            EntryPrice       = 1.1000m,
            SuggestedLotSize = 0m,
            Confidence       = 0.8m,
            ExpiresAt        = DateTime.UtcNow.AddHours(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.SuggestedLotSize)
              .WithErrorMessage("SuggestedLotSize must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Confidence_Exceeds_One()
    {
        var command = new CreateTradeSignalCommand
        {
            StrategyId       = 1,
            Symbol           = "EURUSD",
            Direction        = "Buy",
            EntryPrice       = 1.1000m,
            SuggestedLotSize = 0.01m,
            Confidence       = 1.5m,
            ExpiresAt        = DateTime.UtcNow.AddHours(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Confidence)
              .WithErrorMessage("Confidence must be between 0 and 1");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Confidence_Is_Negative()
    {
        var command = new CreateTradeSignalCommand
        {
            StrategyId       = 1,
            Symbol           = "EURUSD",
            Direction        = "Buy",
            EntryPrice       = 1.1000m,
            SuggestedLotSize = 0.01m,
            Confidence       = -0.1m,
            ExpiresAt        = DateTime.UtcNow.AddHours(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Confidence)
              .WithErrorMessage("Confidence must be between 0 and 1");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new CreateTradeSignalCommand
        {
            StrategyId       = 1,
            Symbol           = "EURUSD",
            Direction        = "Buy",
            EntryPrice       = 1.1000m,
            SuggestedLotSize = 0.01m,
            Confidence       = 0.85m,
            ExpiresAt        = DateTime.UtcNow.AddHours(1)
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success()
    {
        var command = new CreateTradeSignalCommand
        {
            StrategyId       = 1,
            Symbol           = "EURUSD",
            Direction        = "Buy",
            EntryPrice       = 1.1000m,
            SuggestedLotSize = 0.01m,
            Confidence       = 0.85m,
            ExpiresAt        = DateTime.UtcNow.AddHours(1)
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
    }

    [Fact]
    public async Task Handler_Should_Return_Success_With_ML_Fields()
    {
        var command = new CreateTradeSignalCommand
        {
            StrategyId             = 1,
            Symbol                 = "GBPUSD",
            Direction              = "Sell",
            EntryPrice             = 1.2500m,
            SuggestedLotSize       = 0.05m,
            Confidence             = 0.92m,
            MLPredictedDirection   = "Sell",
            MLPredictedMagnitude   = 25m,
            MLConfidenceScore      = 0.90m,
            MLModelId              = 10,
            MLEnsembleDisagreement = 0.03m,
            MLScoringLatencyMs     = 45,
            ExpiresAt              = DateTime.UtcNow.AddHours(2)
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
    }
}
