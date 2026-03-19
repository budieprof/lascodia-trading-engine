using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.RiskProfiles.Commands.CreateRiskProfile;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.RiskProfiles;

public class CreateRiskProfileCommandTest
{
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly CreateRiskProfileCommandHandler _handler;
    private readonly CreateRiskProfileCommandValidator _validator;

    public CreateRiskProfileCommandTest()
    {
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();

        var mockDbContext   = new Mock<DbContext>();
        var riskProfiles    = new List<RiskProfile>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<RiskProfile>()).Returns(riskProfiles.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        _handler   = new CreateRiskProfileCommandHandler(_mockWriteContext.Object);
        _validator = new CreateRiskProfileCommandValidator();
    }

    [Fact]
    public async Task Validator_Should_Fail_When_Name_Is_Empty()
    {
        var command = new CreateRiskProfileCommand
        {
            Name                = string.Empty,
            MaxLotSizePerTrade  = 1m,
            MaxDailyDrawdownPct = 2m,
            MaxTotalDrawdownPct = 10m,
            MaxOpenPositions    = 5,
            MaxDailyTrades      = 10,
            MaxRiskPerTradePct  = 1m,
            MaxSymbolExposurePct         = 5m,
            DrawdownRecoveryThresholdPct = 1.5m,
            RecoveryLotSizeMultiplier    = 0.5m,
            RecoveryExitThresholdPct     = 0.5m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Name)
              .WithErrorMessage("Name cannot be empty");
    }

    [Fact]
    public async Task Validator_Should_Fail_When_MaxLotSize_Zero()
    {
        var command = new CreateRiskProfileCommand
        {
            Name                = "Test Profile",
            MaxLotSizePerTrade  = 0m,
            MaxDailyDrawdownPct = 2m,
            MaxTotalDrawdownPct = 10m,
            MaxOpenPositions    = 5,
            MaxDailyTrades      = 10,
            MaxRiskPerTradePct  = 1m,
            MaxSymbolExposurePct         = 5m,
            DrawdownRecoveryThresholdPct = 1.5m,
            RecoveryLotSizeMultiplier    = 0.5m,
            RecoveryExitThresholdPct     = 0.5m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.MaxLotSizePerTrade)
              .WithErrorMessage("MaxLotSizePerTrade must be greater than zero");
    }

    [Fact]
    public async Task Validator_Should_Pass_With_Valid_Command()
    {
        var command = new CreateRiskProfileCommand
        {
            Name                = "Conservative",
            MaxLotSizePerTrade  = 0.5m,
            MaxDailyDrawdownPct = 1m,
            MaxTotalDrawdownPct = 5m,
            MaxOpenPositions    = 3,
            MaxDailyTrades      = 5,
            MaxRiskPerTradePct  = 0.5m,
            MaxSymbolExposurePct         = 2m,
            DrawdownRecoveryThresholdPct = 1m,
            RecoveryLotSizeMultiplier    = 0.25m,
            RecoveryExitThresholdPct     = 0.25m
        };

        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Should_Return_Success()
    {
        var command = new CreateRiskProfileCommand
        {
            Name                = "Conservative",
            MaxLotSizePerTrade  = 0.5m,
            MaxDailyDrawdownPct = 1m,
            MaxTotalDrawdownPct = 5m,
            MaxOpenPositions    = 3,
            MaxDailyTrades      = 5,
            MaxRiskPerTradePct  = 0.5m,
            MaxSymbolExposurePct         = 2m,
            DrawdownRecoveryThresholdPct = 1m,
            RecoveryLotSizeMultiplier    = 0.25m,
            RecoveryExitThresholdPct     = 0.25m
        };

        _mockWriteContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
        Assert.Equal("Successful", result.message);
    }
}
