using AutoMapper;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.SignalRejectionAuditNs.Queries.DTOs;
using LascodiaTradingEngine.Application.SignalRejectionAuditNs.Queries.GetPagedSignalRejections;
using Lascodia.Trading.Engine.SharedLibrary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using DomainEntity = LascodiaTradingEngine.Domain.Entities.SignalRejectionAudit;

namespace LascodiaTradingEngine.UnitTest.Application.SignalRejectionAuditTests;

/// <summary>
/// Covers the filter composition in <see cref="GetPagedSignalRejectionsQueryHandler"/>.
/// The DB layer is an in-memory DbContext so the LINQ expressions execute
/// against real C# predicates — if any <c>x.Field == filter.X</c> regressed,
/// these tests would catch it.
/// </summary>
public class GetPagedSignalRejectionsQueryTest
{
    private readonly IMapper _mapper = BuildMapper();

    private static IMapper BuildMapper()
    {
        var expr = new MapperConfigurationExpression();
        expr.CreateMap<GetPagedSignalRejectionsQuery, Pager>()
            .ForMember(p => p.TotalItemCount, o => o.Ignore());
        expr.CreateMap<DomainEntity, SignalRejectionAuditDto>();
        return new MapperConfiguration(expr, NullLoggerFactory.Instance).CreateMapper();
    }

    private static FakeReadContext NewCtx()
    {
        var opts = new DbContextOptionsBuilder<FakeReadContext>()
            .UseInMemoryDatabase($"sigrej-{Guid.NewGuid()}").Options;
        return new FakeReadContext(opts);
    }

    private static DomainEntity Row(long id, string stage, string reason, string symbol, long strategyId, long? tradeSignalId, DateTime at) =>
        new()
        {
            Id = id,
            Stage = stage,
            Reason = reason,
            Symbol = symbol,
            StrategyId = strategyId,
            TradeSignalId = tradeSignalId,
            Source = "SW",
            RejectedAt = at,
        };

    [Fact]
    public async Task Handler_Returns_All_Rows_In_Descending_RejectedAt_Order_When_No_Filter()
    {
        using var ctx = NewCtx();
        var t0 = new DateTime(2026, 04, 01, 0, 0, 0, DateTimeKind.Utc);
        ctx.Set<DomainEntity>().AddRange(
            Row(1, "Regime", "blocked", "EURUSD", 10, 100, t0),
            Row(2, "MTF", "missing", "EURUSD", 10, 101, t0.AddMinutes(5)),
            Row(3, "Tier1", "rr_low", "GBPUSD", 11, 102, t0.AddMinutes(10)));
        ctx.SaveChanges();

        var handler = new GetPagedSignalRejectionsQueryHandler(ctx, _mapper);
        var q = new GetPagedSignalRejectionsQuery { ItemCountPerPage = 50, CurrentPage = 1 };

        var resp = await handler.Handle(q, CancellationToken.None);

        Assert.True(resp.status);
        Assert.Equal("00", resp.responseCode);
        Assert.Equal(3, resp.data!.data.Count);
        Assert.Equal(3, resp.data.data[0].Id); // newest first
        Assert.Equal(2, resp.data.data[1].Id);
        Assert.Equal(1, resp.data.data[2].Id);
    }

    [Fact]
    public async Task Handler_Filters_By_Stage_Reason_Symbol_Together()
    {
        using var ctx = NewCtx();
        var t0 = new DateTime(2026, 04, 01, 0, 0, 0, DateTimeKind.Utc);
        ctx.Set<DomainEntity>().AddRange(
            Row(1, "Regime", "blocked", "EURUSD", 10, 100, t0),
            Row(2, "Regime", "blocked", "GBPUSD", 10, 101, t0.AddMinutes(5)),
            Row(3, "Regime", "timeout", "EURUSD", 10, 102, t0.AddMinutes(10)),
            Row(4, "MTF",    "blocked", "EURUSD", 10, 103, t0.AddMinutes(15)));
        ctx.SaveChanges();

        var handler = new GetPagedSignalRejectionsQueryHandler(ctx, _mapper);
        var q = new GetPagedSignalRejectionsQuery
        {
            ItemCountPerPage = 50,
            CurrentPage = 1,
            Filter = new SignalRejectionQueryFilter { Stage = "Regime", Reason = "blocked", Symbol = "EURUSD" },
        };

        var resp = await handler.Handle(q, CancellationToken.None);

        Assert.True(resp.status);
        Assert.Single(resp.data!.data);
        Assert.Equal(1, resp.data.data[0].Id);
    }

    [Fact]
    public async Task Handler_Filters_By_Time_Window()
    {
        using var ctx = NewCtx();
        var t0 = new DateTime(2026, 04, 15, 0, 0, 0, DateTimeKind.Utc);
        ctx.Set<DomainEntity>().AddRange(
            Row(1, "Regime", "blocked", "EURUSD", 10, 100, t0.AddDays(-5)),
            Row(2, "Regime", "blocked", "EURUSD", 10, 101, t0.AddDays(-2)),
            Row(3, "Regime", "blocked", "EURUSD", 10, 102, t0.AddDays(-1)),
            Row(4, "Regime", "blocked", "EURUSD", 10, 103, t0));
        ctx.SaveChanges();

        var handler = new GetPagedSignalRejectionsQueryHandler(ctx, _mapper);
        var q = new GetPagedSignalRejectionsQuery
        {
            ItemCountPerPage = 50,
            CurrentPage = 1,
            Filter = new SignalRejectionQueryFilter { From = t0.AddDays(-3), To = t0.AddDays(-1) },
        };

        var resp = await handler.Handle(q, CancellationToken.None);
        Assert.Equal(2, resp.data!.data.Count);
        Assert.Contains(resp.data.data, d => d.Id == 2);
        Assert.Contains(resp.data.data, d => d.Id == 3);
    }

    [Fact]
    public async Task Handler_Filters_By_StrategyId_And_TradeSignalId()
    {
        using var ctx = NewCtx();
        var t0 = new DateTime(2026, 04, 15, 0, 0, 0, DateTimeKind.Utc);
        ctx.Set<DomainEntity>().AddRange(
            Row(1, "Regime", "blocked", "EURUSD", 10, 500, t0),
            Row(2, "Regime", "blocked", "EURUSD", 11, 500, t0.AddMinutes(1)),
            Row(3, "Regime", "blocked", "EURUSD", 10, 501, t0.AddMinutes(2)));
        ctx.SaveChanges();

        var handler = new GetPagedSignalRejectionsQueryHandler(ctx, _mapper);

        var qByStrategy = new GetPagedSignalRejectionsQuery
        {
            ItemCountPerPage = 50,
            CurrentPage = 1,
            Filter = new SignalRejectionQueryFilter { StrategyId = 10 },
        };
        var byStrategy = await handler.Handle(qByStrategy, CancellationToken.None);
        Assert.Equal(2, byStrategy.data!.data.Count);

        var qBySignal = new GetPagedSignalRejectionsQuery
        {
            ItemCountPerPage = 50,
            CurrentPage = 1,
            Filter = new SignalRejectionQueryFilter { TradeSignalId = 500 },
        };
        var bySignal = await handler.Handle(qBySignal, CancellationToken.None);
        Assert.Equal(2, bySignal.data!.data.Count);
    }

    [Fact]
    public async Task Handler_Paginates_Results_Using_PagerRequest_Fields()
    {
        using var ctx = NewCtx();
        var t0 = new DateTime(2026, 04, 01, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 12; i++)
        {
            ctx.Set<DomainEntity>().Add(Row(i + 1, "Regime", "blocked", "EURUSD", 10, 1000 + i, t0.AddMinutes(i)));
        }
        ctx.SaveChanges();

        var handler = new GetPagedSignalRejectionsQueryHandler(ctx, _mapper);

        var page1 = await handler.Handle(new GetPagedSignalRejectionsQuery { ItemCountPerPage = 5, CurrentPage = 1 }, CancellationToken.None);
        var page2 = await handler.Handle(new GetPagedSignalRejectionsQuery { ItemCountPerPage = 5, CurrentPage = 2 }, CancellationToken.None);
        var page3 = await handler.Handle(new GetPagedSignalRejectionsQuery { ItemCountPerPage = 5, CurrentPage = 3 }, CancellationToken.None);

        Assert.Equal(5, page1.data!.data.Count);
        Assert.Equal(5, page2.data!.data.Count);
        Assert.Equal(2, page3.data!.data.Count);
        Assert.Equal(12, page1.data.pager.TotalItemCount);
    }
}

internal sealed class FakeReadContext : DbContext, IReadApplicationDbContext
{
    public FakeReadContext(DbContextOptions<FakeReadContext> options) : base(options) { }

    public DbContext GetDbContext() => this;

    public new int SaveChanges() => base.SaveChanges();

    public new Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => base.SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DomainEntity>().HasKey(e => e.Id);
        modelBuilder.Entity<LascodiaTradingEngine.Domain.Entities.CalibrationSnapshot>().HasKey(e => e.Id);
    }
}
