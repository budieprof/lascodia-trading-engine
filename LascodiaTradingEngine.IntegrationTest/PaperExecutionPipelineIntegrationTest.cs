using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.PaperTrading.Services;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;

namespace LascodiaTradingEngine.IntegrationTest;

/// <summary>
/// End-to-end coverage for the paper-execution forward-test pipeline against a real
/// Postgres. Verifies that the router opens rows with TCA-realistic fill prices and
/// that the gate-4 query shape correctly distinguishes synthetic from real rows.
/// </summary>
public class PaperExecutionPipelineIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PaperExecutionPipelineIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
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

    /// <summary>Fake TCA provider returning a fixed profile — avoids the real DB lookup
    /// for <c>TransactionCostAnalysis</c> rows in an integration test focused on paper-routing.</summary>
    private sealed class FixedTcaProvider : ITcaCostModelProvider
    {
        public Task<SymbolCostProfile> GetAsync(string symbol, CancellationToken ct)
            => Task.FromResult(new SymbolCostProfile(
                Symbol: symbol,
                AvgSpreadCostInPrice: 0.00010m,
                AvgCommissionCostInAccountCcy: 0.00007m,
                AvgMarketImpactInPrice: 0.00002m,
                SampleSize: 200, IsDefault: false));
    }

    /// <summary>Adapter that exposes a <see cref="WriteApplicationDbContext"/> as
    /// <see cref="LascodiaTradingEngine.Application.Common.Interfaces.IWriteApplicationDbContext"/>
    /// so the router's constructor can be satisfied without the full DI container.</summary>
    private sealed class WriteAdapter : LascodiaTradingEngine.Application.Common.Interfaces.IWriteApplicationDbContext
    {
        private readonly WriteApplicationDbContext _db;
        public WriteAdapter(WriteApplicationDbContext db) => _db = db;
        public Microsoft.EntityFrameworkCore.DbContext GetDbContext() => _db;
        public int SaveChanges() => _db.SaveChanges();
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => _db.SaveChangesAsync(cancellationToken);
    }

    [Fact]
    public async Task Router_OpensRowWithTcaAdjustedFillPrice_ForApprovedPausedStrategy()
    {
        await EnsureMigratedAsync();

        await using (var ctx = CreateWriteContext())
        {
            ctx.Set<Strategy>().Add(new Strategy
            {
                Id                      = 1001,
                Name                    = "test-paper",
                Symbol                  = "EURUSD",
                Timeframe               = Timeframe.H1,
                Status                  = StrategyStatus.Paused,
                LifecycleStage          = StrategyLifecycleStage.Approved,
                LifecycleStageEnteredAt = DateTime.UtcNow.AddDays(-1),
                StrategyType            = StrategyType.CompositeML,
                ParametersJson          = "{}",
            });
            ctx.Set<CurrencyPair>().Add(new CurrencyPair
            {
                Symbol        = "EURUSD",
                DecimalPlaces = 5,
                ContractSize  = 100_000m,
                MinLotSize    = 0.01m,
                MaxLotSize    = 100m,
                LotStep       = 0.01m,
                IsActive      = true,
            });
            await ctx.SaveChangesAsync();
        }

        await using var writeCtx = CreateWriteContext();
        var router = new PaperExecutionRouter(
            new WriteAdapter(writeCtx),
            new FixedTcaProvider(),
            NullLogger<PaperExecutionRouter>.Instance);

        var strategy = await writeCtx.Set<Strategy>().FirstAsync(s => s.Id == 1001);
        var signal = new PaperSignalIntent(
            Direction:           TradeDirection.Buy,
            RequestedEntryPrice: 1.10000m,
            LotSize:             0.10m,
            StopLoss:            1.09800m,
            TakeProfit:          1.10400m,
            GeneratedAtUtc:      DateTime.UtcNow,
            TradeSignalId:       null);

        await router.EnqueueAsync(strategy, signal, (Bid: 1.09998m, Ask: 1.10002m), CancellationToken.None);

        await using var verify = CreateWriteContext();
        var row = await verify.Set<PaperExecution>()
            .FirstOrDefaultAsync(p => p.StrategyId == 1001);
        Assert.NotNull(row);
        Assert.Equal(PaperExecutionStatus.Open, row.Status);
        Assert.False(row.IsSynthetic);
        Assert.Equal(TradeDirection.Buy, row.Direction);
        // Buy fill = Ask + halfSpread + impact = 1.10002 + 0.00005 + 0.00002 = 1.10009
        Assert.Equal(1.10009m, row.SimulatedFillPrice);
        Assert.Equal(0.10m, row.LotSize);
        Assert.Equal(100_000m, row.ContractSize);
        Assert.NotNull(row.TcaProfileSnapshotJson);
        Assert.Contains("EURUSD", row.TcaProfileSnapshotJson);
    }

    [Fact]
    public async Task Gate4_ExcludesSyntheticRowsFromHardCount()
    {
        await EnsureMigratedAsync();

        await using (var ctx = CreateWriteContext())
        {
            ctx.Set<Strategy>().Add(new Strategy
            {
                Id = 2001, Name = "synth-test", Symbol = "EURUSD", Timeframe = Timeframe.H1,
                Status = StrategyStatus.Paused, LifecycleStage = StrategyLifecycleStage.Approved,
                StrategyType = StrategyType.CompositeML, ParametersJson = "{}",
            });
            for (int i = 0; i < 5; i++)
            {
                ctx.Set<PaperExecution>().Add(new PaperExecution
                {
                    StrategyId  = 2001, Symbol = "EURUSD", Timeframe = Timeframe.H1,
                    Direction   = TradeDirection.Buy, Status = PaperExecutionStatus.Closed,
                    SignalGeneratedAt = DateTime.UtcNow.AddHours(-i),
                    SimulatedFillPrice = 1.1m, RequestedEntryPrice = 1.1m,
                    SimulatedFillAt = DateTime.UtcNow, LotSize = 0.01m,
                    ContractSize = 100_000m, PipSize = 0.0001m,
                    IsSynthetic = i < 3, // 3 synthetic, 2 real
                });
            }
            await ctx.SaveChangesAsync();
        }

        await using var verify = CreateWriteContext();
        int realCount = await verify.Set<PaperExecution>()
            .CountAsync(p => p.StrategyId == 2001 && !p.IsSynthetic && p.Status == PaperExecutionStatus.Closed);
        int syntheticCount = await verify.Set<PaperExecution>()
            .CountAsync(p => p.StrategyId == 2001 && p.IsSynthetic);
        int totalCount = await verify.Set<PaperExecution>()
            .CountAsync(p => p.StrategyId == 2001);

        Assert.Equal(2, realCount);
        Assert.Equal(3, syntheticCount);
        Assert.Equal(5, totalCount);
    }
}
