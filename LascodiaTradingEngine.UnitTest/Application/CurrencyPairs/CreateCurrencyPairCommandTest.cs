using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.CurrencyPairs.Commands.CreateCurrencyPair;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.CurrencyPairs;

public class CreateCurrencyPairCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly CreateCurrencyPairCommandHandler _handler;
    private readonly CreateCurrencyPairCommandValidator _validator;

    public CreateCurrencyPairCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();

        var mockDbContext = new Mock<DbContext>();
        var pairs = new List<CurrencyPair>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<CurrencyPair>()).Returns(pairs.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler   = new CreateCurrencyPairCommandHandler(_mockWriteContext.Object);
        _validator = new CreateCurrencyPairCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Symbol_Is_Empty()
    {
        var command = new CreateCurrencyPairCommand
        {
            Symbol        = string.Empty,
            BaseCurrency  = "EUR",
            QuoteCurrency = "USD"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Symbol)
              .WithErrorMessage("Symbol cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_BaseCurrency_Is_Wrong_Length()
    {
        var command = new CreateCurrencyPairCommand
        {
            Symbol        = "EURUSD",
            BaseCurrency  = "EU",
            QuoteCurrency = "USD"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.BaseCurrency)
              .WithErrorMessage("BaseCurrency must be 3 characters");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_MaxLotSize_Less_Than_MinLotSize()
    {
        var command = new CreateCurrencyPairCommand
        {
            Symbol        = "EURUSD",
            BaseCurrency  = "EUR",
            QuoteCurrency = "USD",
            MinLotSize    = 10m,
            MaxLotSize    = 5m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.MaxLotSize)
              .WithErrorMessage("MaxLotSize must be greater than MinLotSize");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new CreateCurrencyPairCommand
        {
            Symbol        = "EURUSD",
            BaseCurrency  = "EUR",
            QuoteCurrency = "USD"
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success()
    {
        var command = new CreateCurrencyPairCommand
        {
            Symbol        = "EURUSD",
            BaseCurrency  = "EUR",
            QuoteCurrency = "USD"
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
    }

    [Fact]
    public async Task Handler_Should_Uppercase_Symbol()
    {
        CurrencyPair? capturedEntity = null;

        var mockDbContext = new Mock<DbContext>();
        var pairs = new List<CurrencyPair>().AsQueryable().BuildMockDbSet();
        pairs.Setup(x => x.AddAsync(It.IsAny<CurrencyPair>(), It.IsAny<CancellationToken>()))
             .Callback<CurrencyPair, CancellationToken>((e, _) => capturedEntity = e)
             .Returns(new ValueTask<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<CurrencyPair>>());
        mockDbContext.Setup(c => c.Set<CurrencyPair>()).Returns(pairs.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var command = new CreateCurrencyPairCommand
        {
            Symbol        = "eurusd",
            BaseCurrency  = "eur",
            QuoteCurrency = "usd"
        };

        await _handler.Handle(command, CancellationToken.None);

        Assert.NotNull(capturedEntity);
        Assert.Equal("EURUSD", capturedEntity!.Symbol);
        Assert.Equal("EUR", capturedEntity.BaseCurrency);
        Assert.Equal("USD", capturedEntity.QuoteCurrency);
    }
}
