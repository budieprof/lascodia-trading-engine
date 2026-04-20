using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLEvaluation.Commands.RecordPredictionOutcome;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.MLEvaluation;

public class RecordPredictionOutcomeCommandTest
{
    [Fact]
    public async Task Handle_Writes_ConformalNonconformityScore_When_Outcome_Is_Recorded()
    {
        var logs = new List<MLModelPredictionLog>
        {
            new()
            {
                Id = 1,
                TradeSignalId = 42,
                MLModelId = 7,
                PredictedDirection = TradeDirection.Buy,
                ConfidenceScore = 0.70m,
                ServedCalibratedProbability = 0.80m,
                ConformalThresholdUsed = 0.30,
                ConformalPredictionSetJson = "[\"Buy\"]"
            }
        };

        var mockSet = logs.AsQueryable().BuildMockDbSet();
        var mockDb = new Mock<DbContext>();
        mockDb.Setup(x => x.Set<MLModelPredictionLog>()).Returns(mockSet.Object);

        var mockContext = new Mock<IWriteApplicationDbContext>();
        mockContext.Setup(x => x.GetDbContext()).Returns(mockDb.Object);
        mockContext.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var handler = new RecordPredictionOutcomeCommandHandler(mockContext.Object);

        var response = await handler.Handle(new RecordPredictionOutcomeCommand
        {
            TradeSignalId = 42,
            ActualDirection = "Sell",
            ActualMagnitudePips = 12.5m,
            WasProfitable = false
        }, CancellationToken.None);

        Assert.True(response.status);
        Assert.Equal(TradeDirection.Sell, logs[0].ActualDirection);
        Assert.False(logs[0].DirectionCorrect);
        Assert.Equal(0.80, logs[0].ConformalNonConformityScore!.Value, precision: 6);
        Assert.False(logs[0].WasConformalCovered);
    }
}
