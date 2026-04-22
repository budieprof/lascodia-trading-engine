using AutoMapper;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;
using LascodiaTradingEngine.Application.MLModels.Queries.GetMLSignalAbTestResult;
using LascodiaTradingEngine.Application.MLModels.Queries.GetPagedMLSignalAbTestResults;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LascodiaTradingEngine.UnitTest.Application.MLModels;

public class GetPagedMLSignalAbTestResultsQueryTest
{
    private readonly IMapper _mapper = BuildMapper();

    [Fact]
    public async Task Handler_Returns_Results_In_Descending_CompletedAt_Order()
    {
        using var ctx = NewCtx();
        var t0 = new DateTime(2026, 04, 01, 0, 0, 0, DateTimeKind.Utc);
        ctx.Set<MLSignalAbTestResult>().AddRange(
            Row(1, "EURUSD", Timeframe.H1, "RejectChallenger", t0, championModelId: 10, challengerModelId: 20),
            Row(2, "EURUSD", Timeframe.H1, "PromoteChallenger", t0.AddHours(2), championModelId: 10, challengerModelId: 21),
            Row(3, "GBPUSD", Timeframe.M15, "KeepRunning", t0.AddHours(1), championModelId: 11, challengerModelId: 22));
        ctx.SaveChanges();

        var handler = new GetPagedMLSignalAbTestResultsQueryHandler(ctx, _mapper);

        var resp = await handler.Handle(
            new GetPagedMLSignalAbTestResultsQuery { ItemCountPerPage = 50, CurrentPage = 1 },
            CancellationToken.None);

        Assert.True(resp.status);
        Assert.Equal("00", resp.responseCode);
        Assert.Equal([2, 3, 1], resp.data!.data.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task Handler_Applies_Model_Market_Decision_And_Time_Window_Filters()
    {
        using var ctx = NewCtx();
        var t0 = new DateTime(2026, 04, 10, 0, 0, 0, DateTimeKind.Utc);
        ctx.Set<MLSignalAbTestResult>().AddRange(
            Row(1, "EURUSD", Timeframe.H1, "PromoteChallenger", t0, 10, 20),
            Row(2, "EURUSD", Timeframe.H4, "PromoteChallenger", t0.AddHours(1), 10, 20),
            Row(3, "EURUSD", Timeframe.H1, "RejectChallenger", t0.AddHours(2), 10, 20),
            Row(4, "GBPUSD", Timeframe.H1, "PromoteChallenger", t0.AddHours(3), 10, 20),
            Row(5, "EURUSD", Timeframe.H1, "PromoteChallenger", t0.AddHours(4), 10, 21));
        ctx.SaveChanges();

        var handler = new GetPagedMLSignalAbTestResultsQueryHandler(ctx, _mapper);
        var query = new GetPagedMLSignalAbTestResultsQuery
        {
            ItemCountPerPage = 50,
            CurrentPage = 1,
            Filter = new MLSignalAbTestResultQueryFilter
            {
                ChampionModelId = 10,
                ChallengerModelId = 20,
                Symbol = "eurusd",
                Timeframe = "H1",
                Decision = "promotechallenger",
                CompletedFromUtc = t0.AddMinutes(-1),
                CompletedToUtc = t0.AddMinutes(1),
            },
        };

        var resp = await handler.Handle(query, CancellationToken.None);

        var result = Assert.Single(resp.data!.data);
        Assert.Equal(1, result.Id);
        Assert.Equal(Timeframe.H1, result.Timeframe);
        Assert.Equal("PromoteChallenger", result.Decision);
    }

    [Fact]
    public async Task Handler_Paginates_With_Total_Count()
    {
        using var ctx = NewCtx();
        var t0 = new DateTime(2026, 04, 20, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 12; i++)
            ctx.Set<MLSignalAbTestResult>().Add(Row(i + 1, "EURUSD", Timeframe.H1, "RejectChallenger", t0.AddMinutes(i), 10, 20 + i));
        ctx.SaveChanges();

        var handler = new GetPagedMLSignalAbTestResultsQueryHandler(ctx, _mapper);

        var page1 = await handler.Handle(new GetPagedMLSignalAbTestResultsQuery { ItemCountPerPage = 5, CurrentPage = 1 }, CancellationToken.None);
        var page3 = await handler.Handle(new GetPagedMLSignalAbTestResultsQuery { ItemCountPerPage = 5, CurrentPage = 3 }, CancellationToken.None);

        Assert.Equal(5, page1.data!.data.Count);
        Assert.Equal(2, page3.data!.data.Count);
        Assert.Equal(12, page1.data.pager.TotalItemCount);
    }

    [Fact]
    public async Task GetById_Returns_NotFound_For_Missing_Result()
    {
        using var ctx = NewCtx();
        var handler = new GetMLSignalAbTestResultQueryHandler(ctx, _mapper);

        var resp = await handler.Handle(new GetMLSignalAbTestResultQuery { Id = 404 }, CancellationToken.None);

        Assert.False(resp.status);
        Assert.Equal("-14", resp.responseCode);
    }

    private static IMapper BuildMapper()
    {
        var expr = new MapperConfigurationExpression();
        expr.CreateMap<GetPagedMLSignalAbTestResultsQuery, Pager>()
            .ForMember(p => p.TotalItemCount, o => o.Ignore());
        expr.CreateMap<MLSignalAbTestResult, MLSignalAbTestResultDto>();
        return new MapperConfiguration(expr, NullLoggerFactory.Instance).CreateMapper();
    }

    private static MLSignalAbTestReadContext NewCtx()
    {
        var opts = new DbContextOptionsBuilder<MLSignalAbTestReadContext>()
            .UseInMemoryDatabase($"ml-signal-abtest-results-{Guid.NewGuid()}")
            .Options;
        return new MLSignalAbTestReadContext(opts);
    }

    private static MLSignalAbTestResult Row(
        long id,
        string symbol,
        Timeframe timeframe,
        string decision,
        DateTime completedAt,
        long championModelId,
        long challengerModelId)
        => new()
        {
            Id = id,
            ChampionModelId = championModelId,
            ChallengerModelId = challengerModelId,
            Symbol = symbol,
            Timeframe = timeframe,
            StartedAtUtc = completedAt.AddHours(-6),
            CompletedAtUtc = completedAt,
            Decision = decision,
            Reason = "test",
            ChampionTradeCount = 31,
            ChallengerTradeCount = 32,
            ChampionAvgPnl = 1.1m,
            ChallengerAvgPnl = 1.4m,
            ChampionSharpe = 0.8m,
            ChallengerSharpe = 1.1m,
            SprtLogLikelihoodRatio = 3.2m,
            CovariateImbalanceScore = 0.12m,
        };

    private sealed class MLSignalAbTestReadContext : DbContext, IReadApplicationDbContext
    {
        public MLSignalAbTestReadContext(DbContextOptions<MLSignalAbTestReadContext> options) : base(options) { }

        public DbContext GetDbContext() => this;

        public new int SaveChanges() => base.SaveChanges();

        public new Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => base.SaveChangesAsync(cancellationToken);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MLSignalAbTestResult>().HasKey(e => e.Id);
        }
    }
}
