using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.IntegrationTest;

public class MLErgodicityWorkerPostgresIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public MLErgodicityWorkerPostgresIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunCycleAsync_Writes_Ergodicity_Logs_On_Postgres()
    {
        await EnsureMigratedAsync();

        await using var seedContext = CreateContext();
        var first = CreateModel("EURUSD");
        var second = CreateModel("GBPUSD");
        var strategy = new Strategy
        {
            Name = $"ml-ergodicity-postgres-{Guid.NewGuid():N}",
            StrategyType = StrategyType.CompositeML,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = "{}",
            Status = StrategyStatus.Active
        };

        seedContext.Set<MLModel>().AddRange(first, second);
        seedContext.Set<Strategy>().Add(strategy);
        await seedContext.SaveChangesAsync();

        var now = DateTime.UtcNow;
        await AddPredictionLogsAsync(seedContext, strategy.Id, first, now, correctCount: 24, incorrectCount: 6, startTradeSignalId: 1);
        await AddPredictionLogsAsync(seedContext, strategy.Id, second, now, correctCount: 8, incorrectCount: 22, startTradeSignalId: 100);
        await seedContext.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new DbContextAccessor(CreateContext()));
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<DbContextAccessor>());
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<DbContextAccessor>());
        await using var provider = services.BuildServiceProvider();

        var worker = new MLErgodicityWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLErgodicityWorker>.Instance,
            options: new MLErgodicityOptions
            {
                MinSamples = 20,
                MaxLogsPerModel = 30,
                ModelBatchSize = 1,
                ReturnPipScale = 100,
                MaxReturnAbs = 0.50
            });

        await worker.RunCycleAsync(CancellationToken.None);

        await using var assertContext = CreateContext();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", assertContext.Database.ProviderName);
        var logs = await assertContext.Set<MLErgodicityLog>()
            .OrderBy(l => l.MLModelId)
            .ToListAsync();

        Assert.Equal(2, logs.Count);
        Assert.All(logs, log =>
        {
            Assert.NotEqual(0m, log.GrowthRateVariance);
            Assert.InRange(log.NaiveKellyFraction, -2m, 2m);
            Assert.InRange(log.ErgodicityAdjustedKelly, -2m, 2m);
        });
        Assert.True(logs[0].EnsembleGrowthRate > logs[1].EnsembleGrowthRate);
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

    private static MLModel CreateModel(string symbol) => new()
    {
        Symbol = symbol,
        Timeframe = Timeframe.H1,
        ModelVersion = "1.0.0",
        FilePath = $"/tmp/{symbol}.json",
        Status = MLModelStatus.Active,
        IsActive = true,
        TrainingSamples = 100,
        TrainedAt = DateTime.UtcNow.AddDays(-10),
        ActivatedAt = DateTime.UtcNow.AddDays(-5)
    };

    private static async Task AddPredictionLogsAsync(
        WriteApplicationDbContext context,
        long strategyId,
        MLModel model,
        DateTime now,
        int correctCount,
        int incorrectCount,
        long startTradeSignalId)
    {
        int total = correctCount + incorrectCount;
        for (int i = 0; i < total; i++)
        {
            bool correct = i < correctCount;
            var predictedAt = now.AddMinutes(-total + i);
            var signal = new TradeSignal
            {
                StrategyId = strategyId,
                Symbol = model.Symbol,
                Direction = TradeDirection.Buy,
                EntryPrice = 1.1000m,
                SuggestedLotSize = 0.10m,
                Confidence = 0.80m,
                Status = TradeSignalStatus.Approved,
                GeneratedAt = predictedAt,
                ExpiresAt = predictedAt.AddHours(1)
            };

            context.Set<TradeSignal>().Add(signal);
            context.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
            {
                TradeSignal = signal,
                MLModelId = model.Id,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                PredictedDirection = TradeDirection.Buy,
                PredictedAt = predictedAt,
                OutcomeRecordedAt = predictedAt,
                DirectionCorrect = correct,
                WasProfitable = correct,
                ActualDirection = correct ? TradeDirection.Buy : TradeDirection.Sell,
                ActualMagnitudePips = correct ? 12m : -10m,
                ConfidenceScore = 0.75m
            });
        }

        await Task.CompletedTask;
    }

    private sealed class DbContextAccessor(WriteApplicationDbContext context)
        : IReadApplicationDbContext, IWriteApplicationDbContext, IAsyncDisposable
    {
        public DbContext GetDbContext() => context;
        public int SaveChanges() => context.SaveChanges();
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => context.SaveChangesAsync(cancellationToken);
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
