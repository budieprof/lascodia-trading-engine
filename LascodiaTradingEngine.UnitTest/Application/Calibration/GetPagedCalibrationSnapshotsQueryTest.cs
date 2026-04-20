using AutoMapper;
using LascodiaTradingEngine.Application.Calibration.Queries.DTOs;
using LascodiaTradingEngine.Application.Calibration.Queries.GetPagedCalibrationSnapshots;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.UnitTest.Application.SignalRejectionAuditTests;
using Lascodia.Trading.Engine.SharedLibrary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using DomainSnap = LascodiaTradingEngine.Domain.Entities.CalibrationSnapshot;

namespace LascodiaTradingEngine.UnitTest.Application.Calibration;

/// <summary>
/// Covers the filter composition and ordering of
/// <see cref="GetPagedCalibrationSnapshotsQueryHandler"/>.
/// </summary>
public class GetPagedCalibrationSnapshotsQueryTest
{
    private readonly IMapper _mapper = BuildMapper();

    private static IMapper BuildMapper()
    {
        var expr = new MapperConfigurationExpression();
        expr.CreateMap<GetPagedCalibrationSnapshotsQuery, Pager>()
            .ForMember(p => p.TotalItemCount, o => o.Ignore());
        expr.CreateMap<DomainSnap, CalibrationSnapshotDto>();
        return new MapperConfiguration(expr, NullLoggerFactory.Instance).CreateMapper();
    }

    private static FakeReadContext NewCtx()
    {
        var opts = new DbContextOptionsBuilder<FakeReadContext>()
            .UseInMemoryDatabase($"calsnap-{Guid.NewGuid()}").Options;
        return new FakeReadContext(opts);
    }

    private static DomainSnap Snap(long id, DateTime periodStart, string stage, string reason,
                                    long count, string granularity = "Monthly") =>
        new()
        {
            Id = id,
            PeriodStart = periodStart,
            PeriodEnd = periodStart.AddMonths(1),
            PeriodGranularity = granularity,
            Stage = stage,
            Reason = reason,
            RejectionCount = count,
            DistinctSymbols = 1,
            DistinctStrategies = 1,
            ComputedAt = periodStart.AddDays(1),
        };

    [Fact]
    public async Task Handler_Orders_By_PeriodStart_Desc_Then_Stage_Then_Reason()
    {
        using var ctx = NewCtx();
        var jan = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc);
        ctx.Set<DomainSnap>().AddRange(
            Snap(1, jan,              "Regime", "blocked", 100),
            Snap(2, jan.AddMonths(1), "MTF",    "missing", 50),
            Snap(3, jan.AddMonths(1), "Regime", "blocked", 80));
        ctx.SaveChanges();

        var handler = new GetPagedCalibrationSnapshotsQueryHandler(ctx, _mapper);
        var resp = await handler.Handle(new GetPagedCalibrationSnapshotsQuery { ItemCountPerPage = 50, CurrentPage = 1 }, CancellationToken.None);

        Assert.True(resp.status);
        Assert.Equal(3, resp.data!.data.Count);
        // Desc by PeriodStart (Feb first), then asc by Stage ("MTF" < "Regime").
        Assert.Equal(2, resp.data.data[0].Id); // Feb / MTF
        Assert.Equal(3, resp.data.data[1].Id); // Feb / Regime
        Assert.Equal(1, resp.data.data[2].Id); // Jan
    }

    [Fact]
    public async Task Handler_Filters_By_Stage_Reason_Granularity()
    {
        using var ctx = NewCtx();
        var jan = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc);
        ctx.Set<DomainSnap>().AddRange(
            Snap(1, jan, "Regime", "blocked", 100),
            Snap(2, jan, "Regime", "timeout", 20),
            Snap(3, jan, "MTF",    "blocked", 30),
            Snap(4, jan, "Regime", "blocked", 10, granularity: "Weekly"));
        ctx.SaveChanges();

        var handler = new GetPagedCalibrationSnapshotsQueryHandler(ctx, _mapper);

        var q = new GetPagedCalibrationSnapshotsQuery
        {
            ItemCountPerPage = 50,
            CurrentPage = 1,
            Filter = new CalibrationSnapshotQueryFilter { Stage = "Regime", Reason = "blocked", PeriodGranularity = "Monthly" },
        };
        var resp = await handler.Handle(q, CancellationToken.None);

        Assert.Single(resp.data!.data);
        Assert.Equal(1, resp.data.data[0].Id);
    }

    [Fact]
    public async Task Handler_Filters_By_Period_Window()
    {
        using var ctx = NewCtx();
        var jan = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc);
        ctx.Set<DomainSnap>().AddRange(
            Snap(1, jan.AddMonths(-2), "Regime", "blocked", 50),
            Snap(2, jan.AddMonths(-1), "Regime", "blocked", 60),
            Snap(3, jan,               "Regime", "blocked", 70),
            Snap(4, jan.AddMonths(1),  "Regime", "blocked", 80));
        ctx.SaveChanges();

        var handler = new GetPagedCalibrationSnapshotsQueryHandler(ctx, _mapper);
        var q = new GetPagedCalibrationSnapshotsQuery
        {
            ItemCountPerPage = 50,
            CurrentPage = 1,
            Filter = new CalibrationSnapshotQueryFilter
            {
                From = jan.AddMonths(-1),
                To   = jan,
            },
        };
        var resp = await handler.Handle(q, CancellationToken.None);

        Assert.Equal(2, resp.data!.data.Count);
        Assert.Contains(resp.data.data, d => d.Id == 2);
        Assert.Contains(resp.data.data, d => d.Id == 3);
    }
}
