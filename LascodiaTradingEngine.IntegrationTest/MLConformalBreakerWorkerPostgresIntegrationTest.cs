using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.Metrics;

namespace LascodiaTradingEngine.IntegrationTest;

/// <summary>
/// End-to-end Postgres exercise of <see cref="MLConformalBreakerWorker"/>'s atomic
/// chronic-trip alert upsert path. The unit-test suite uses InMemoryDatabase, which
/// does not execute the worker's PostgreSQL-specific
/// <c>INSERT ... ON CONFLICT WHERE ... DO NOTHING</c> SQL. This test exercises that SQL
/// directly against a real Postgres container so a typo in the column list, the
/// partial-unique-index conflict target, or the parameter binding would surface as a
/// failing test rather than a production incident.
/// </summary>
public class MLConformalBreakerWorkerPostgresIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public MLConformalBreakerWorkerPostgresIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunCycleAsync_ChronicTripUpsert_IsAtomicAndIdempotentOnPostgres()
    {
        await EnsureMigratedAsync();

        var nowUtc = DateTime.UtcNow;

        await using var seedContext = CreateContext();
        var model = CreateModel("EURUSD");
        seedContext.Set<MLModel>().Add(model);
        await seedContext.SaveChangesAsync();

        // Active breaker that's been refreshed for many cycles → eligible for chronic-trip.
        var existingBreaker = new MLConformalBreakerLog
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            ConsecutivePoorCoverageBars = 8,
            SampleCount = 30,
            CoveredCount = 5,
            EmpiricalCoverage = 0.16,
            TargetCoverage = 0.90,
            CoverageThreshold = 0.50,
            TripReason = MLConformalBreakerTripReason.Both,
            SuspensionBars = 16,
            SuspendedAt = nowUtc.AddHours(-2),
            ResumeAt = nowUtc.AddHours(8),
            IsActive = true
        };
        seedContext.Set<MLConformalBreakerLog>().Add(existingBreaker);

        seedContext.Set<MLConformalCalibration>().Add(new MLConformalCalibration
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            CalibrationSamples = 30,
            TargetCoverage = 0.90,
            CoverageThreshold = 0.50,
            CalibratedAt = nowUtc.AddDays(-1)
        });

        // Pre-seed TripStreak at the threshold so the first cycle crosses it.
        seedContext.Set<EngineConfig>().Add(new EngineConfig
        {
            Key = $"MLConformal:Model:{model.Id}:TripStreak",
            Value = "3",
            DataType = ConfigDataType.Int,
            IsHotReloadable = false,
            LastUpdatedAt = nowUtc,
            IsDeleted = false
        });

        // Add 30 uncovered prediction logs after the suspension so the active breaker
        // refreshes (still bad) → updatedTripStreaks[model.Id] becomes 4.
        var strategy = new Strategy
        {
            Name = $"conformal-breaker-pg-{Guid.NewGuid():N}",
            StrategyType = StrategyType.CompositeML,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            ParametersJson = "{}",
            Status = StrategyStatus.Active
        };
        seedContext.Set<Strategy>().Add(strategy);
        await seedContext.SaveChangesAsync();
        AddPredictionLogs(seedContext, strategy.Id, model, existingBreaker.SuspendedAt.AddMinutes(1), covered: false);
        await seedContext.SaveChangesAsync();

        var dispatcher = new RecordingAlertDispatcher();
        var dedupKey = $"ml-conformal-chronic-trip:{model.Id}";

        // ── First cycle: chronic alert should be INSERT-ed via atomic SQL. ──
        await RunWorkerCycleAsync(dispatcher);

        await using (var assertCtx = CreateContext())
        {
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", assertCtx.Database.ProviderName);

            var alerts = await assertCtx.Set<Alert>()
                .Where(a => a.DeduplicationKey == dedupKey)
                .ToListAsync();
            Assert.Single(alerts);
            Assert.Equal(AlertType.MLModelDegraded, alerts[0].AlertType);
            Assert.Equal(AlertSeverity.High, alerts[0].Severity);
            Assert.True(alerts[0].IsActive);
        }

        // ── Second cycle: same scenario, atomic INSERT should DO NOTHING (no duplicate). ──
        // Reset the streak so this cycle would re-cross the threshold and re-attempt the
        // INSERT — we want to verify ON CONFLICT no-ops, not just that the read-then-add
        // path returns the existing row.
        await using (var resetCtx = CreateContext())
        {
            var streakRow = await resetCtx.Set<EngineConfig>()
                .SingleAsync(c => c.Key == $"MLConformal:Model:{model.Id}:TripStreak");
            streakRow.Value = "3";
            await resetCtx.SaveChangesAsync();
        }

        await RunWorkerCycleAsync(dispatcher);

        await using (var assertCtx = CreateContext())
        {
            var alerts = await assertCtx.Set<Alert>()
                .Where(a => a.DeduplicationKey == dedupKey)
                .ToListAsync();
            Assert.Single(alerts); // exactly one — atomic INSERT no-op on second attempt
        }
    }

    private async Task RunWorkerCycleAsync(IAlertDispatcher dispatcher)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new DbContextAccessor(CreateContext()));
        services.AddScoped<IReadApplicationDbContext>(sp => sp.GetRequiredService<DbContextAccessor>());
        services.AddScoped<IWriteApplicationDbContext>(sp => sp.GetRequiredService<DbContextAccessor>());
        await using var provider = services.BuildServiceProvider();

        var metrics = new TradingMetrics(new TestMeterFactory());
        var worker = new MLConformalBreakerWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MLConformalBreakerWorker>.Instance,
            new MLConformalBreakerOptions
            {
                MinLogs = 30,
                MaxLogs = 200,
                ConsecutiveUncoveredTrigger = 3,
                InitialDelayMinutes = 0,
                ChronicTripThreshold = 4
            },
            metrics,
            dispatcher,
            new MLConformalCoverageEvaluator(),
            new MLConformalPredictionLogReader(),
            new MLConformalCalibrationReader(),
            new MLConformalBreakerStateStore(
                NullLogger<MLConformalBreakerStateStore>.Instance,
                metrics));

        await worker.RunAsync(CancellationToken.None);
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

    private static void AddPredictionLogs(
        WriteApplicationDbContext context,
        long strategyId,
        MLModel model,
        DateTime firstOutcomeAt,
        bool covered)
    {
        for (int i = 0; i < 30; i++)
        {
            var predictedAt = firstOutcomeAt.AddMinutes(i);
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
                ExpiresAt = predictedAt.AddHours(1),
                MLModelId = model.Id
            };
            context.Set<TradeSignal>().Add(signal);
            context.Set<MLModelPredictionLog>().Add(new MLModelPredictionLog
            {
                TradeSignal = signal,
                MLModelId = model.Id,
                ModelRole = ModelRole.Champion,
                Symbol = model.Symbol,
                Timeframe = model.Timeframe,
                PredictedDirection = TradeDirection.Buy,
                ConfidenceScore = 0.80m,
                ActualDirection = TradeDirection.Buy,
                OutcomeRecordedAt = predictedAt,
                PredictedAt = predictedAt,
                WasConformalCovered = covered
            });
        }
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);
        public void Dispose() { }
    }

    private sealed class RecordingAlertDispatcher : IAlertDispatcher
    {
        public Task DispatchAsync(Alert alert, string message, CancellationToken ct)
        {
            alert.LastTriggeredAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        public Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct)
        {
            alert.AutoResolvedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }
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
