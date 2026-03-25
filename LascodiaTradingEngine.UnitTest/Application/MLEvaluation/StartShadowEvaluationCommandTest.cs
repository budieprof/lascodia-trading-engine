using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLEvaluation.Commands.StartShadowEvaluation;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.MLEvaluation;

public class StartShadowEvaluationCommandTest
{
    private readonly StartShadowEvaluationCommandValidator _validator = new();

    [Fact]
    public async Task Validator_Fails_When_ChallengerModelId_Zero()
    {
        var cmd = new StartShadowEvaluationCommand { ChallengerModelId = 0, ChampionModelId = 1, Symbol = "EURUSD", Timeframe = "H1" };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldHaveValidationErrorFor(c => c.ChallengerModelId);
    }

    [Fact]
    public async Task Validator_Fails_When_Symbol_Empty()
    {
        var cmd = new StartShadowEvaluationCommand { ChallengerModelId = 1, ChampionModelId = 2, Symbol = "", Timeframe = "H1" };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldHaveValidationErrorFor(c => c.Symbol);
    }

    [Fact]
    public async Task Validator_Fails_When_RequiredTrades_OutOfRange()
    {
        var cmd = new StartShadowEvaluationCommand { ChallengerModelId = 1, ChampionModelId = 2, Symbol = "EURUSD", Timeframe = "H1", RequiredTrades = 5 };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldHaveValidationErrorFor(c => c.RequiredTrades);
    }

    [Fact]
    public async Task Validator_Passes_Valid_Command()
    {
        var cmd = new StartShadowEvaluationCommand { ChallengerModelId = 1, ChampionModelId = 2, Symbol = "EURUSD", Timeframe = "H1", RequiredTrades = 50 };
        var result = await _validator.TestValidateAsync(cmd);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Handler_Creates_Evaluation_Successfully()
    {
        var mockContext = new Mock<IWriteApplicationDbContext>();
        var mockDbContext = new Mock<DbContext>();
        var evals = new List<MLShadowEvaluation>().AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<MLShadowEvaluation>()).Returns(evals.Object);
        mockContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);
        mockContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var handler = new StartShadowEvaluationCommandHandler(mockContext.Object);
        var cmd = new StartShadowEvaluationCommand { ChallengerModelId = 1, ChampionModelId = 2, Symbol = "EURUSD", Timeframe = "H1" };

        var result = await handler.Handle(cmd, CancellationToken.None);

        Assert.True(result.status);
        Assert.Equal("00", result.responseCode);
    }
}
