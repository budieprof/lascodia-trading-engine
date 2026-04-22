using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Services.ML;

public class CpcEncoderPromotionServiceTest
{
    [Fact]
    public async Task PromoteAsync_Rotates_Active_Row_And_Invalidates_Cache()
    {
        await using var db = CreateDbContext();
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EncoderType = CpcEncoderType.Linear,
            EmbeddingDim = 16,
            InfoNceLoss = 2.0,
            EncoderBytes = [1],
            TrainedAt = DateTime.UtcNow.AddDays(-10),
            IsActive = true
        });
        await db.SaveChangesAsync();

        var cache = new Mock<IActiveCpcEncoderProvider>();
        var service = new CpcEncoderPromotionService(cache.Object);
        var candidate = NewEncoder(loss: 1.0);

        var result = await service.PromoteAsync(
            db,
            new CpcEncoderPromotionRequest("EURUSD", Timeframe.H1, null, PriorEncoderId: 1, MinImprovement: 0.02),
            candidate,
            CancellationToken.None);

        Assert.True(result.Promoted);
        Assert.False((await db.Set<MLCpcEncoder>().SingleAsync(e => e.Id == 1)).IsActive);
        Assert.True(candidate.IsActive);
        Assert.True(candidate.Id > 0);
        cache.Verify(c => c.Invalidate("EURUSD", Timeframe.H1, null), Times.Once);
    }

    [Fact]
    public async Task PromoteAsync_Skips_When_Newer_Better_Active_Appeared()
    {
        await using var db = CreateDbContext();
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 2,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EncoderType = CpcEncoderType.Linear,
            EmbeddingDim = 16,
            InfoNceLoss = 1.0,
            EncoderBytes = [2],
            TrainedAt = DateTime.UtcNow,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var cache = new Mock<IActiveCpcEncoderProvider>();
        var service = new CpcEncoderPromotionService(cache.Object);
        var candidate = NewEncoder(loss: 1.6);

        var result = await service.PromoteAsync(
            db,
            new CpcEncoderPromotionRequest("EURUSD", Timeframe.H1, null, PriorEncoderId: 1, MinImprovement: 0.02),
            candidate,
            CancellationToken.None);

        Assert.False(result.Promoted);
        Assert.Equal("superseded_by_better_active", result.Reason);
        Assert.True((await db.Set<MLCpcEncoder>().SingleAsync(e => e.Id == 2)).IsActive);
        Assert.False(await db.Set<MLCpcEncoder>().AnyAsync(e => e.EncoderBytes != null && e.EncoderBytes.SequenceEqual(new byte[] { 3 })));
        cache.Verify(c => c.Invalidate(It.IsAny<string>(), It.IsAny<Timeframe>(), It.IsAny<MarketRegime?>()), Times.Never);
    }

    private static MLCpcEncoder NewEncoder(double loss)
        => new()
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EncoderType = CpcEncoderType.Linear,
            EmbeddingDim = 16,
            PredictionSteps = 3,
            InfoNceLoss = loss,
            EncoderBytes = [3],
            TrainedAt = DateTime.UtcNow,
            IsActive = true
        };

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }
}
