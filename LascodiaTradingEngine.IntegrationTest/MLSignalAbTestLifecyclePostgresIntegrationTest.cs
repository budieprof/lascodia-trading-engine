using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;

namespace LascodiaTradingEngine.IntegrationTest;

public class MLSignalAbTestLifecyclePostgresIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public MLSignalAbTestLifecyclePostgresIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TerminalResultStore_UsesPostgresUniqueIndexAndRemainsIdempotent()
    {
        await EnsureMigratedAsync();
        var startedAt = new DateTime(2026, 04, 22, 10, 0, 0, DateTimeKind.Utc);
        var state = State(startedAt);
        var result = Result(AbTestDecision.PromoteChallenger);

        await using (var db = CreateWriteContext())
        {
            var store = new SignalAbTestTerminalResultStore();
            await store.PersistAsync(db, state, result, CancellationToken.None);
            await store.PersistAsync(db, state, result, CancellationToken.None);
        }

        await using (var assertDb = CreateWriteContext())
        {
            var audit = Assert.Single(await assertDb.Set<MLSignalAbTestResult>().AsNoTracking().ToListAsync());
            Assert.Equal("PromoteChallenger", audit.Decision);
            Assert.Equal(startedAt, audit.StartedAtUtc);
        }

        await using var duplicateDb = CreateWriteContext();
        duplicateDb.Set<MLSignalAbTestResult>().Add(new MLSignalAbTestResult
        {
            ChampionModelId = state.ChampionModelId,
            ChallengerModelId = state.ChallengerModelId,
            Symbol = state.Symbol,
            Timeframe = state.Timeframe,
            StartedAtUtc = state.StartedAtUtc,
            CompletedAtUtc = DateTime.UtcNow,
            Decision = result.Decision.ToString(),
            Reason = "raw duplicate should fail",
            ChampionTradeCount = result.ChampionTradeCount,
            ChallengerTradeCount = result.ChallengerTradeCount,
            ChampionAvgPnl = (decimal)result.ChampionAvgPnl,
            ChallengerAvgPnl = (decimal)result.ChallengerAvgPnl,
            ChampionSharpe = (decimal)result.ChampionSharpe,
            ChallengerSharpe = (decimal)result.ChallengerSharpe,
            SprtLogLikelihoodRatio = (decimal)result.SprtLogLikelihoodRatio,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => duplicateDb.SaveChangesAsync());
    }

    [Fact]
    public async Task TerminalResultAndPromotion_RollBackTogether_WhenTransactionDoesNotCommit()
    {
        await EnsureMigratedAsync();

        long championId;
        long otherActiveId;
        long challengerId;
        await using (var seedDb = CreateWriteContext())
        {
            var champion = Model("1.0.1", MLModelStatus.Active, isActive: true, accuracy: 0.61m);
            var otherActive = Model("1.0.3", MLModelStatus.Active, isActive: true, accuracy: 0.55m);
            var challenger = Model("1.0.2", MLModelStatus.Training, isActive: false, accuracy: 0.67m);

            seedDb.Set<MLModel>().AddRange(champion, otherActive, challenger);
            await seedDb.SaveChangesAsync();

            championId = champion.Id;
            otherActiveId = otherActive.Id;
            challengerId = challenger.Id;
        }

        await using (var txDb = CreateWriteContext())
        {
            await using var tx = await txDb.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var state = State(new DateTime(2026, 04, 22, 11, 0, 0, DateTimeKind.Utc), championId, challengerId);
            var store = new SignalAbTestTerminalResultStore();
            var lifecycle = new MLModelLifecycleTransitionService(
                NullLogger<MLModelLifecycleTransitionService>.Instance);

            await store.PersistAsync(txDb, state, Result(AbTestDecision.PromoteChallenger), CancellationToken.None);
            await lifecycle.PromoteChallengerAsync(
                txDb, championId, challengerId, "EURUSD", Timeframe.H1, CancellationToken.None);

            await tx.RollbackAsync();
        }

        await using var assertDb = CreateWriteContext();
        Assert.Empty(await assertDb.Set<MLSignalAbTestResult>().AsNoTracking().ToListAsync());
        Assert.Empty(await assertDb.Set<MLModelLifecycleLog>().AsNoTracking().ToListAsync());

        var models = await assertDb.Set<MLModel>().AsNoTracking().ToListAsync();
        Assert.True(models.Single(m => m.Id == championId).IsActive);
        Assert.True(models.Single(m => m.Id == otherActiveId).IsActive);
        Assert.False(models.Single(m => m.Id == challengerId).IsActive);
        Assert.Equal(MLModelStatus.Active, models.Single(m => m.Id == championId).Status);
        Assert.Equal(MLModelStatus.Training, models.Single(m => m.Id == challengerId).Status);
    }

    [Fact]
    public async Task PromotionLifecycle_UsesCanonicalEventTypes_OnPostgres()
    {
        await EnsureMigratedAsync();

        long championId;
        long challengerId;
        await using (var seedDb = CreateWriteContext())
        {
            var champion = Model("1.0.1", MLModelStatus.Active, isActive: true, accuracy: 0.61m);
            var challenger = Model("1.0.2", MLModelStatus.Training, isActive: false, accuracy: 0.67m);
            seedDb.Set<MLModel>().AddRange(champion, challenger);
            await seedDb.SaveChangesAsync();
            championId = champion.Id;
            challengerId = challenger.Id;
        }

        await using (var db = CreateWriteContext())
        {
            var lifecycle = new MLModelLifecycleTransitionService(
                NullLogger<MLModelLifecycleTransitionService>.Instance);
            await lifecycle.PromoteChallengerAsync(
                db, championId, challengerId, "EURUSD", Timeframe.H1, CancellationToken.None);
        }

        await using var assertDb = CreateWriteContext();
        var logs = await assertDb.Set<MLModelLifecycleLog>().AsNoTracking().ToListAsync();
        Assert.Contains(logs, x => x.MLModelId == challengerId &&
                                   x.EventType == MLModelLifecycleEventType.AbTestPromotion);
        Assert.Contains(logs, x => x.MLModelId == championId &&
                                   x.EventType == MLModelLifecycleEventType.AbTestDemotion);
    }

    [Fact]
    public async Task PromotionLifecycle_ConcurrentAttempts_DoNotDuplicateAuditRows_OnPostgres()
    {
        await EnsureMigratedAsync();

        long championId;
        long otherActiveId;
        long challengerId;
        await using (var seedDb = CreateWriteContext())
        {
            var champion = Model("1.0.1", MLModelStatus.Active, isActive: true, accuracy: 0.61m);
            var otherActive = Model("1.0.3", MLModelStatus.Active, isActive: true, accuracy: 0.55m);
            var challenger = Model("1.0.2", MLModelStatus.Training, isActive: false, accuracy: 0.67m);
            seedDb.Set<MLModel>().AddRange(champion, otherActive, challenger);
            await seedDb.SaveChangesAsync();
            championId = champion.Id;
            otherActiveId = otherActive.Id;
            challengerId = challenger.Id;
        }

        var first = PromoteInFreshContextAsync(championId, challengerId);
        var second = PromoteInFreshContextAsync(championId, challengerId);
        var results = await Task.WhenAll(first, second);

        Assert.InRange(results.Count(x => x is null), 1, 2);
        Assert.All(results.Where(x => x is not null), ex => Assert.IsType<DbUpdateException>(ex));

        await using var assertDb = CreateWriteContext();
        var logs = await assertDb.Set<MLModelLifecycleLog>()
            .AsNoTracking()
            .ToListAsync();
        Assert.Single(logs, x => x.MLModelId == challengerId &&
                                 x.EventType == MLModelLifecycleEventType.AbTestPromotion &&
                                 x.PreviousChampionModelId == championId);
        Assert.Single(logs, x => x.MLModelId == championId &&
                                 x.EventType == MLModelLifecycleEventType.AbTestDemotion);

        var models = await assertDb.Set<MLModel>()
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Id);
        Assert.False(models[championId].IsActive);
        Assert.False(models[otherActiveId].IsActive);
        Assert.True(models[challengerId].IsActive);
        Assert.Equal(MLModelStatus.Superseded, models[championId].Status);
        Assert.Equal(MLModelStatus.Superseded, models[otherActiveId].Status);
        Assert.Equal(MLModelStatus.Active, models[challengerId].Status);
    }

    private async Task<Exception?> PromoteInFreshContextAsync(long championId, long challengerId)
    {
        try
        {
            await using var db = CreateWriteContext();
            var lifecycle = new MLModelLifecycleTransitionService(
                NullLogger<MLModelLifecycleTransitionService>.Instance);
            await lifecycle.PromoteChallengerAsync(
                db, championId, challengerId, "EURUSD", Timeframe.H1, CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private WriteApplicationDbContext CreateWriteContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private async Task EnsureMigratedAsync()
    {
        await using var context = CreateWriteContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();
    }

    private static AbTestState State(DateTime startedAt, long championId = 1, long challengerId = 2)
        => new(
            TestId: 0,
            ChampionModelId: championId,
            ChallengerModelId: challengerId,
            Symbol: "EURUSD",
            Timeframe: Timeframe.H1,
            StartedAtUtc: startedAt,
            ChampionOutcomes: [],
            ChallengerOutcomes: []);

    private static AbTestResult Result(AbTestDecision decision)
        => new()
        {
            Decision = decision,
            Reason = "integration test",
            ChampionTradeCount = 40,
            ChallengerTradeCount = 41,
            ChampionAvgPnl = 1.25,
            ChallengerAvgPnl = 2.5,
            ChampionSharpe = 0.7,
            ChallengerSharpe = 1.2,
            SprtLogLikelihoodRatio = 3.1,
        };

    private static MLModel Model(string version, MLModelStatus status, bool isActive, decimal accuracy)
        => new()
        {
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ModelVersion = version,
            FilePath = string.Empty,
            Status = status,
            IsActive = isActive,
            DirectionAccuracy = accuracy,
            BrierScore = 0.2m,
            ActivatedAt = isActive ? DateTime.UtcNow.AddDays(-2) : null,
            ModelBytes = [1, 2, 3],
        };
}
