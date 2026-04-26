using System.Text.Json;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.IntegrationTest;

/// <summary>
/// End-to-end Postgres exercise of <see cref="LascodiaTradingEngine.Application.Workers.MLCalibratedEdgeWorker"/>'s
/// concurrency-sensitive paths. Unit tests run on shared-cache SQLite which doesn't
/// model Postgres' partial-unique-index conflict semantics. These tests run the same
/// SQL the worker emits in production against a real Postgres container.
/// </summary>
public class MLCalibratedEdgeWorkerPostgresIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public MLCalibratedEdgeWorkerPostgresIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ConcurrentAlertUpsertWithSameDedupKey_YieldsExactlyOneActiveAlert()
    {
        await EnsureMigratedAsync();

        // Four concurrent attempts to upsert an Alert with the same DeduplicationKey.
        // The unique partial index on (DeduplicationKey) WHERE IsActive AND NOT IsDeleted
        // must serialise the inserts so exactly one wins.
        const string dedupKey = "ml-calibrated-edge:777";

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
            catch (DbUpdateException) { /* loser */ }
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
        // (Symbol, Timeframe). The unique partial index on (Symbol, Timeframe) WHERE
        // Status IN ('Queued','Running') AND NOT IsDeleted must serialise the inserts.
        const string symbol = "USDJPY";
        const Timeframe tf = Timeframe.M15;
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
                    DriftTriggerType = "CalibratedEdge",
                    DriftMetadataJson = "{}",
                    Priority = 2,
                    IsDeleted = false,
                });
                await ctx.SaveChangesAsync();
            }
            catch (DbUpdateException) { /* loser */ }
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
    public async Task ConcurrentChronicAlertUpsert_YieldsExactlyOneActiveAlert()
    {
        await EnsureMigratedAsync();

        // Four concurrent attempts to upsert the chronic-tripper alert for the same
        // model. Same atomic semantics as the per-model alert.
        const long modelId = 8888888;
        string dedupKey = $"ml-calibrated-edge-chronic:{modelId}";

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
                    ConditionJson = JsonSerializer.Serialize(new { modelId, streak }),
                    IsActive = true,
                });
                await ctx.SaveChangesAsync();
            }
            catch (DbUpdateException) { /* loser */ }
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
