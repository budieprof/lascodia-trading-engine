using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;

namespace LascodiaTradingEngine.IntegrationTest;

public class MLConformalPredictionLogReaderIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public MLConformalPredictionLogReaderIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LoadRecentResolvedLogsByModelAsync_Uses_Postgres_Window_Per_Model_With_Stable_Order()
    {
        await EnsureMigratedAsync();

        await using var seedContext = CreateContext();
        var modelA = CreateModel("EURUSD", "1.0.0");
        var modelB = CreateModel("GBPUSD", "1.0.0");
        seedContext.Set<MLModel>().AddRange(modelA, modelB);
        var strategy = new Strategy
        {
            Name = "conformal-reader-test",
            StrategyType = StrategyType.CompositeML,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = "{}",
            Status = StrategyStatus.Active
        };
        seedContext.Set<Strategy>().Add(strategy);
        await seedContext.SaveChangesAsync();

        var baseTime = DateTime.UtcNow.AddHours(-2);
        var olderA = await AddPredictionLogAsync(seedContext, strategy.Id, modelA, baseTime.AddMinutes(1), true);
        var middleA = await AddPredictionLogAsync(seedContext, strategy.Id, modelA, baseTime.AddMinutes(2), false);
        var lowerTieA = await AddPredictionLogAsync(seedContext, strategy.Id, modelA, baseTime.AddMinutes(3), true);
        var higherTieA = await AddPredictionLogAsync(seedContext, strategy.Id, modelA, baseTime.AddMinutes(3), false);
        await AddPredictionLogAsync(seedContext, strategy.Id, modelA, baseTime.AddMinutes(4), true, isDeleted: true);
        await AddPredictionLogAsync(seedContext, strategy.Id, modelA, baseTime.AddMinutes(5), true, resolved: false);

        var oldB = await AddPredictionLogAsync(seedContext, strategy.Id, modelB, baseTime.AddMinutes(10), true);
        var newestB = await AddPredictionLogAsync(seedContext, strategy.Id, modelB, baseTime.AddMinutes(11), false);
        await AddPredictionLogAsync(seedContext, strategy.Id, modelB, baseTime.AddMinutes(12), true, resolved: false);

        await using var readContext = CreateContext();
        var reader = new MLConformalPredictionLogReader();

        var logsByModel = await reader.LoadRecentResolvedLogsByModelAsync(
            readContext,
            [modelA.Id, modelB.Id],
            maxLogs: 3,
            CancellationToken.None);

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", readContext.Database.ProviderName);
        Assert.True(logsByModel.ContainsKey(modelA.Id));
        Assert.True(logsByModel.ContainsKey(modelB.Id));

        Assert.Equal(
            [higherTieA.Id, lowerTieA.Id, middleA.Id],
            logsByModel[modelA.Id].Select(l => l.Id).ToArray());
        Assert.DoesNotContain(logsByModel[modelA.Id], l => l.Id == olderA.Id);

        Assert.Equal(
            [newestB.Id, oldB.Id],
            logsByModel[modelB.Id].Select(l => l.Id).ToArray());
        Assert.All(logsByModel.Values.SelectMany(x => x), log =>
        {
            Assert.NotNull(log.ActualDirection);
            Assert.NotNull(log.OutcomeRecordedAt);
            Assert.False(log.IsDeleted);
        });
    }

    private WriteApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private async Task EnsureMigratedAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();
    }

    private static MLModel CreateModel(string symbol, string version) => new()
    {
        Symbol = symbol,
        Timeframe = Timeframe.H1,
        ModelVersion = version,
        FilePath = $"/tmp/{symbol}-{version}.json",
        Status = MLModelStatus.Active,
        IsActive = true,
        TrainingSamples = 100,
        TrainedAt = DateTime.UtcNow.AddDays(-10),
        ActivatedAt = DateTime.UtcNow.AddDays(-5)
    };

    private static async Task<MLModelPredictionLog> AddPredictionLogAsync(
        WriteApplicationDbContext context,
        long strategyId,
        MLModel model,
        DateTime outcomeRecordedAt,
        bool covered,
        bool resolved = true,
        bool isDeleted = false)
    {
        var signal = new TradeSignal
        {
            StrategyId = strategyId,
            Symbol = model.Symbol,
            Direction = TradeDirection.Buy,
            EntryPrice = 1.1000m,
            SuggestedLotSize = 0.10m,
            Confidence = 0.80m,
            Status = TradeSignalStatus.Approved,
            GeneratedAt = outcomeRecordedAt.AddMinutes(-5),
            ExpiresAt = outcomeRecordedAt.AddHours(1),
            MLModelId = model.Id
        };

        var log = new MLModelPredictionLog
        {
            TradeSignal = signal,
            MLModelId = model.Id,
            ModelRole = ModelRole.Champion,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            PredictedDirection = TradeDirection.Buy,
            PredictedMagnitudePips = 12.5m,
            ConfidenceScore = 0.80m,
            PredictedAt = outcomeRecordedAt.AddMinutes(-5),
            ActualDirection = resolved ? TradeDirection.Buy : null,
            ActualMagnitudePips = resolved ? 8.0m : null,
            OutcomeRecordedAt = resolved ? outcomeRecordedAt : null,
            WasConformalCovered = resolved ? covered : null,
            IsDeleted = isDeleted
        };

        context.Set<MLModelPredictionLog>().Add(log);
        await context.SaveChangesAsync();
        return log;
    }
}
