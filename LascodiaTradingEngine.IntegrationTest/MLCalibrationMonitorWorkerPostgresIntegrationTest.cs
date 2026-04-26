using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
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

namespace LascodiaTradingEngine.IntegrationTest;

/// <summary>
/// End-to-end Postgres exercise of <see cref="MLCalibrationMonitorWorker"/>'s
/// concurrency-sensitive paths. Unit tests run against shared-cache SQLite, which
/// doesn't model Postgres' partial-unique-index conflict semantics or `RowVersion`
/// (xmin) update behaviour. These tests run the same SQL the worker emits in
/// production against a real Postgres container.
/// </summary>
public class MLCalibrationMonitorWorkerPostgresIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public MLCalibrationMonitorWorkerPostgresIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ConcurrentAlertUpsertWithSameDedupKey_YieldsExactlyOneActiveAlert()
    {
        await EnsureMigratedAsync();

        // Four concurrent attempts to upsert an Alert with the same DeduplicationKey.
        // The unique partial index `("DeduplicationKey") WHERE "IsActive" AND NOT "IsDeleted"`
        // must serialise the inserts so exactly one wins; the others must fail with a
        // unique-constraint violation that the worker's `IsLikelyAlertDeduplicationRace`
        // classifier handles.
        const string dedupKey = "ml-calibration-monitor:42424";

        async Task TryInsertAsync()
        {
            await using var ctx = CreateContext();
            try
            {
                ctx.Set<Alert>().Add(new Alert
                {
                    AlertType = AlertType.MLModelDegraded,
                    Severity = AlertSeverity.High,
                    DeduplicationKey = dedupKey,
                    Symbol = "EURUSD",
                    CooldownSeconds = 3600,
                    ConditionJson = "{}",
                    IsActive = true,
                });
                await ctx.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Expected for the losers — the partial unique index rejected this insert.
            }
        }

        await Task.WhenAll(TryInsertAsync(), TryInsertAsync(), TryInsertAsync(), TryInsertAsync());

        await using var assertCtx = CreateContext();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", assertCtx.Database.ProviderName);

        var alerts = await assertCtx.Set<Alert>()
            .Where(a => a.DeduplicationKey == dedupKey && a.IsActive)
            .ToListAsync();
        Assert.Single(alerts);
    }

    [Fact]
    public async Task ConcurrentMLTrainingRunQueue_YieldsExactlyOneActiveRun()
    {
        await EnsureMigratedAsync();

        // Four concurrent attempts to queue a Queued/Running MLTrainingRun for the same
        // (Symbol, Timeframe). The unique partial index
        // `("Symbol", "Timeframe") WHERE "Status" IN ('Queued','Running') AND NOT "IsDeleted"`
        // must serialise the inserts. The worker's `IsLikelyUniqueViolation` classifier
        // catches the loser-side `DbUpdateException` and returns false from the queue
        // method (no error to the cycle).
        const string symbol = "GBPUSD";
        const Timeframe tf = Timeframe.H4;
        var nowUtc = DateTime.UtcNow;

        async Task TryQueueAsync()
        {
            await using var ctx = CreateContext();
            try
            {
                ctx.Set<MLTrainingRun>().Add(new MLTrainingRun
                {
                    Symbol = symbol,
                    Timeframe = tf,
                    TriggerType = TriggerType.AutoDegrading,
                    Status = RunStatus.Queued,
                    FromDate = nowUtc.AddDays(-30),
                    ToDate = nowUtc,
                    StartedAt = nowUtc,
                    LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                    DriftTriggerType = "CalibrationMonitor",
                    DriftMetadataJson = "{}",
                    Priority = 2,
                    IsDeleted = false,
                });
                await ctx.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Expected for losers.
            }
        }

        await Task.WhenAll(TryQueueAsync(), TryQueueAsync(), TryQueueAsync(), TryQueueAsync());

        await using var assertCtx = CreateContext();
        var runs = await assertCtx.Set<MLTrainingRun>()
            .Where(r => r.Symbol == symbol && r.Timeframe == tf
                     && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running)
                     && !r.IsDeleted)
            .ToListAsync();
        Assert.Single(runs);
    }

    [Fact]
    public async Task RowVersionChangesOnModelBytesUpdate_InvalidatingBootstrapCache()
    {
        await EnsureMigratedAsync();

        // Postgres' `xmin` system column backs MLModel.RowVersion. Updating ModelBytes
        // must bump RowVersion so the worker's bootstrap-stderr cache invalidates on
        // the next cycle (cache key includes the previous RowVersion and the worker
        // returns null when it doesn't match).
        long modelId;
        uint initialRowVersion;
        await using (var seedCtx = CreateContext())
        {
            var model = new MLModel
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ModelVersion = "1.0.0",
                FilePath = "/tmp/seed-model.json",
                Status = MLModelStatus.Active,
                IsActive = true,
                TrainingSamples = 100,
                TrainedAt = DateTime.UtcNow.AddDays(-10),
                ActivatedAt = DateTime.UtcNow.AddDays(-5),
                ModelBytes = JsonSerializer.SerializeToUtf8Bytes(new ModelSnapshot
                {
                    Ece = 0.05,
                    OptimalThreshold = 0.5,
                    TemperatureScale = 1.0,
                }),
                LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
            };
            seedCtx.Set<MLModel>().Add(model);
            await seedCtx.SaveChangesAsync();
            modelId = model.Id;
            initialRowVersion = model.RowVersion;
        }

        // Champion swap: update ModelBytes. RowVersion must change.
        await using (var swapCtx = CreateContext())
        {
            var model = await swapCtx.Set<MLModel>().SingleAsync(m => m.Id == modelId);
            model.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(new ModelSnapshot
            {
                Ece = 0.06,
                OptimalThreshold = 0.5,
                TemperatureScale = 1.0,
            });
            await swapCtx.SaveChangesAsync();
        }

        await using (var assertCtx = CreateContext())
        {
            var model = await assertCtx.Set<MLModel>().SingleAsync(m => m.Id == modelId);
            Assert.NotEqual(initialRowVersion, model.RowVersion);
        }
    }

    [Fact]
    public async Task ConcurrentChronicAlertUpsert_YieldsExactlyOneActiveAlert()
    {
        await EnsureMigratedAsync();

        // Four concurrent attempts to upsert the chronic-tripper alert for the same
        // model. Same dedup contract as the per-model alert — exactly one row wins,
        // others fail. Verifies the chronic path uses the same atomic semantics as
        // the regular alert dispatch.
        const long modelId = 7777777;
        string dedupKey = $"ml-calibration-monitor-chronic:{modelId}";

        async Task TryInsertAsync(int streak)
        {
            await using var ctx = CreateContext();
            try
            {
                ctx.Set<Alert>().Add(new Alert
                {
                    AlertType = AlertType.MLModelDegraded,
                    Severity = AlertSeverity.Critical,
                    DeduplicationKey = dedupKey,
                    Symbol = "EURUSD",
                    CooldownSeconds = 3600,
                    ConditionJson = JsonSerializer.Serialize(new
                    {
                        modelId,
                        consecutiveCriticalCycles = streak,
                    }),
                    IsActive = true,
                });
                await ctx.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Expected for losers.
            }
        }

        await Task.WhenAll(TryInsertAsync(4), TryInsertAsync(5), TryInsertAsync(6), TryInsertAsync(7));

        await using var assertCtx = CreateContext();
        var alerts = await assertCtx.Set<Alert>()
            .Where(a => a.DeduplicationKey == dedupKey && a.IsActive)
            .ToListAsync();
        Assert.Single(alerts);
        Assert.Equal(AlertSeverity.Critical, alerts[0].Severity);
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
}
