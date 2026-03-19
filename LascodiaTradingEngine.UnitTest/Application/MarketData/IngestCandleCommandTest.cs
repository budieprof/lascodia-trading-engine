using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketData.Commands.IngestCandle;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.MarketData;

public class IngestCandleCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly IngestCandleCommandHandler _handler;
    private readonly IngestCandleCommandValidator _validator;

    public IngestCandleCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();

        var mockDbContext = new Mock<DbContext>();
        var candles = new List<Candle>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<Candle>()).Returns(candles.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler   = new IngestCandleCommandHandler(_mockWriteContext.Object);
        _validator = new IngestCandleCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Symbol_Is_Empty()
    {
        var command = ValidCommand();
        command.Symbol = string.Empty;

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Symbol)
              .WithErrorMessage("Symbol cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Timeframe_Is_Invalid()
    {
        var command = ValidCommand();
        command.Timeframe = "W1";

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Timeframe);
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Open_Is_Zero()
    {
        var command = ValidCommand();
        command.Open = 0;

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Open)
              .WithErrorMessage("Open must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var result = await _validator.TestValidateAsync(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success_For_New_Candle()
    {
        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(ValidCommand(), CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
    }

    [Theory]
    [InlineData("M1")]
    [InlineData("M5")]
    [InlineData("M15")]
    [InlineData("H1")]
    [InlineData("H4")]
    [InlineData("D1")]
    public async Task Validator_Should_Accept_All_Valid_Timeframes(string timeframe)
    {
        var command = ValidCommand();
        command.Timeframe = timeframe;

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    private static IngestCandleCommand ValidCommand() => new()
    {
        Symbol    = "EURUSD",
        Timeframe = "H1",
        Open      = 1.0850m,
        High      = 1.0870m,
        Low       = 1.0840m,
        Close     = 1.0860m,
        Volume    = 1500m,
        Timestamp = new DateTime(2024, 1, 1, 9, 0, 0, DateTimeKind.Utc),
        IsClosed  = true
    };
}
