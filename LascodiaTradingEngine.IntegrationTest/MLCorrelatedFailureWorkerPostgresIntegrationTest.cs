using LascodiaTradingEngine.Application.Common.Interfaces;
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

public class MLCorrelatedFailureWorkerPostgresIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public MLCorrelatedFailureWorkerPostgresIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunCycleAsync_Activates_Systemic_Pause_On_Postgres()
    {
        await EnsureMigratedAsync();

        await using var seedContext = CreateContext();
        var first = CreateModel("EURUSD");
        var second = CreateModel("GBPUSD");
        var third = CreateModel("USDJPY");
        var strategy = new Strategy
        {
            Name = $"ml-correlated-postgres-{Guid.NewGuid():N}",
            StrategyType = StrategyType.CompositeML,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = "{}",
            Status = StrategyStatus.Active
        };

        seedContext.Set<MLModel>().AddRange(first, second, third);
        seedContext.Set<Strategy>().Add(strategy);
        await seedContext.SaveChangesAsync();

        var now = DateTime.UtcNow;
        await AddPredictionLogsAsync(seedContext, strategy.Id, first, now, correct: false, startTradeSignalId: 1);
        await AddPredictionLogsAsync(seedContext, strategy.Id, second, now, correct: false, startTradeSignalId: 100);
        await AddPredictionLogsAsync(seedContext, strategy.Id, third, now, correct: true, startTradeSignalId: 200);
        await seedContext.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new DbContextAccessor(CreateContext()));
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<DbContextAccessor>());
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<DbContextAccessor>());
        await using var provider = services.BuildServiceProvider();

        var worker = new MLCorrelatedFailureWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLCorrelatedFailureWorker>.Instance);

        await worker.RunCycleAsync(CancellationToken.None);

        await using var assertContext = CreateContext();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", assertContext.Database.ProviderName);
        Assert.Equal("true", await assertContext.Set<EngineConfig>()
            .Where(c => c.Key == "MLTraining:SystemicPauseActive" && !c.IsDeleted)
            .Select(c => c.Value)
            .SingleAsync());
        var log = await assertContext.Set<MLCorrelatedFailureLog>().SingleAsync();
        Assert.Equal(2, log.FailingModelCount);
        Assert.Equal(3, log.EvaluatedModelCount);
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
        bool correct,
        long startTradeSignalId)
    {
        for (int i = 0; i < 30; i++)
        {
            var predictedAt = now.AddMinutes(-30 + i);
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
                PredictedAt = predictedAt,
                OutcomeRecordedAt = predictedAt,
                DirectionCorrect = correct,
                WasProfitable = correct,
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
