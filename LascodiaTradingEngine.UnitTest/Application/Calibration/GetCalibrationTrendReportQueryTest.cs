using LascodiaTradingEngine.Application.Calibration.Queries.GetCalibrationTrendReport;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.UnitTest.Application.SignalRejectionAuditTests;
using LascodiaTradingEngine.UnitTest.Application.Workers;
using Microsoft.EntityFrameworkCore;
using DomainSnap = LascodiaTradingEngine.Domain.Entities.CalibrationSnapshot;

namespace LascodiaTradingEngine.UnitTest.Application.Calibration;

/// <summary>
/// Verifies the recalibration-grade trend report: window selection
/// (latest-complete-month vs N-baseline-months), share computations, and the
/// anomaly flag + volume floor logic. The DB layer is an in-memory context.
/// </summary>
public class GetCalibrationTrendReportQueryTest
{
    // Fixed "now" = 2026-04-20 → latest complete month = March 2026.
    private readonly DateTime _now = new(2026, 04, 20, 0, 0, 0, DateTimeKind.Utc);

    private FakeReadContext NewCtx()
    {
        var opts = new DbContextOptionsBuilder<FakeReadContext>()
            .UseInMemoryDatabase($"trend-{Guid.NewGuid()}").Options;
        return new FakeReadContext(opts);
    }

    private static DomainSnap Snap(long id, DateTime periodStart, string stage, string reason, long count) =>
        new()
        {
            Id = id,
            PeriodStart = periodStart,
            PeriodEnd = periodStart.AddMonths(1),
            PeriodGranularity = "Monthly",
            Stage = stage,
            Reason = reason,
            RejectionCount = count,
            DistinctSymbols = 1,
            DistinctStrategies = 1,
            ComputedAt = periodStart.AddDays(1),
        };

    private GetCalibrationTrendReportQueryHandler NewHandler(IReadApplicationDbContext ctx)
        => new(ctx, new FixedTimeProvider(_now));

    [Fact]
    public async Task Handler_Selects_Latest_Complete_Month_And_Baseline_Window()
    {
        using var ctx = NewCtx();
        var march = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc);
        var dec   = new DateTime(2025, 12, 01, 0, 0, 0, DateTimeKind.Utc);
        ctx.Set<DomainSnap>().AddRange(
            Snap(1, march, "Regime", "blocked", 100),
            Snap(2, dec,   "Regime", "blocked", 100));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(new GetCalibrationTrendReportQuery { BaselineMonths = 3 }, CancellationToken.None);

        Assert.True(resp.status);
        Assert.Equal(march,             resp.data!.LatestMonthStart);
        Assert.Equal(march.AddMonths(1),resp.data.LatestMonthEnd);
        Assert.Equal(dec,               resp.data.BaselineStart);
        Assert.Equal(march,             resp.data.BaselineEnd);
    }

    [Fact]
    public async Task Handler_Flags_Anomaly_When_Share_Rises_Beyond_Threshold()
    {
        using var ctx = NewCtx();
        // Baseline: Regime/blocked = 20% (200/1000). Latest: 60% (600/1000).
        // Delta = +0.40 → exceeds 0.15 threshold AND baseline of 200 >= 30 min.
        var dec = new DateTime(2025, 12, 01, 0, 0, 0, DateTimeKind.Utc);
        var jan = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc);
        var feb = new DateTime(2026, 02, 01, 0, 0, 0, DateTimeKind.Utc);
        var mar = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc);

        ctx.Set<DomainSnap>().AddRange(
            // baseline months — total = 1000, Regime/blocked = 200
            Snap(1, dec, "Regime", "blocked", 60),
            Snap(2, jan, "Regime", "blocked", 70),
            Snap(3, feb, "Regime", "blocked", 70),
            Snap(4, dec, "MTF",    "missing", 200),
            Snap(5, jan, "MTF",    "missing", 300),
            Snap(6, feb, "MTF",    "missing", 300),
            // latest month — total = 1000, Regime/blocked = 600
            Snap(7, mar, "Regime", "blocked", 600),
            Snap(8, mar, "MTF",    "missing", 400));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(new GetCalibrationTrendReportQuery(), CancellationToken.None);

        var regime = resp.data!.Rows.Single(r => r.Stage == "Regime" && r.Reason == "blocked");
        Assert.True(regime.IsAnomaly);
        Assert.Equal(600,  regime.LatestMonthCount);
        Assert.Equal(200,  regime.BaselineCount);
        Assert.Equal(0.60m, regime.LatestMonthSharePct);
        Assert.Equal(0.20m, regime.BaselineSharePct);
        Assert.Equal(0.40m, regime.DeltaPct);
        Assert.NotNull(regime.Hint);
        Assert.Contains("rising", regime.Hint!);
    }

    [Fact]
    public async Task Handler_Does_Not_Flag_When_Baseline_Below_Min_Count()
    {
        using var ctx = NewCtx();
        // Baseline only has 5 rows — below the default 30 floor — so delta
        // must not fire anomaly even if huge.
        var dec = new DateTime(2025, 12, 01, 0, 0, 0, DateTimeKind.Utc);
        var mar = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc);
        ctx.Set<DomainSnap>().AddRange(
            Snap(1, dec, "Abstention", "conformal_width_exceeds_bar", 5),
            Snap(2, mar, "Abstention", "conformal_width_exceeds_bar", 500),
            Snap(3, dec, "MTF", "missing", 1000),
            Snap(4, mar, "MTF", "missing", 1000));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(new GetCalibrationTrendReportQuery(), CancellationToken.None);

        var abstention = resp.data!.Rows.Single(r => r.Stage == "Abstention");
        Assert.False(abstention.IsAnomaly);
        Assert.Null(abstention.Hint);
    }

    [Fact]
    public async Task Handler_Sorts_Anomalies_First_Then_By_Baseline_Volume()
    {
        using var ctx = NewCtx();
        var dec = new DateTime(2025, 12, 01, 0, 0, 0, DateTimeKind.Utc);
        var jan = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc);
        var feb = new DateTime(2026, 02, 01, 0, 0, 0, DateTimeKind.Utc);
        var mar = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc);

        // Baseline total = 3000; latest total = 1000. Three pairs chosen so that:
        //   MTF/missing    share 0.60 → 0.70 (|Δ|=0.10, below threshold → not anomaly, baseline 1800)
        //   Regime/blocked share 0.30 → 0.10 (|Δ|=0.20, ANOMALY, baseline 900)
        //   News/blackout  share 0.10 → 0.20 (|Δ|=0.10, not anomaly, baseline 300)
        ctx.Set<DomainSnap>().AddRange(
            Snap(1, dec, "MTF",    "missing",  600),
            Snap(2, jan, "MTF",    "missing",  600),
            Snap(3, feb, "MTF",    "missing",  600),
            Snap(4, dec, "Regime", "blocked",  300),
            Snap(5, jan, "Regime", "blocked",  300),
            Snap(6, feb, "Regime", "blocked",  300),
            Snap(7, dec, "News",   "blackout", 100),
            Snap(8, jan, "News",   "blackout", 100),
            Snap(9, feb, "News",   "blackout", 100),
            // latest month — total 1000
            Snap(10, mar, "MTF",    "missing",  700),
            Snap(11, mar, "Regime", "blocked",  100),
            Snap(12, mar, "News",   "blackout", 200));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(new GetCalibrationTrendReportQuery(), CancellationToken.None);

        Assert.True(resp.data!.Rows[0].IsAnomaly);
        Assert.Equal("Regime", resp.data.Rows[0].Stage);
        // Remaining non-anomaly rows sorted by baseline volume desc: MTF (1800) > News (300).
        Assert.False(resp.data.Rows[1].IsAnomaly);
        Assert.Equal("MTF", resp.data.Rows[1].Stage);
        Assert.False(resp.data.Rows[2].IsAnomaly);
        Assert.Equal("News", resp.data.Rows[2].Stage);
    }

    [Fact]
    public async Task Handler_Returns_Empty_Report_When_No_Snapshots()
    {
        using var ctx = NewCtx();
        var resp = await NewHandler(ctx).Handle(new GetCalibrationTrendReportQuery(), CancellationToken.None);

        Assert.True(resp.status);
        Assert.Empty(resp.data!.Rows);
        Assert.Equal(0, resp.data.LatestMonthTotal);
        Assert.Equal(0, resp.data.BaselineTotal);
    }

    [Fact]
    public async Task Handler_Handles_Pair_Missing_From_One_Window()
    {
        using var ctx = NewCtx();
        var dec = new DateTime(2025, 12, 01, 0, 0, 0, DateTimeKind.Utc);
        var jan = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc);
        var feb = new DateTime(2026, 02, 01, 0, 0, 0, DateTimeKind.Utc);
        var mar = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc);

        ctx.Set<DomainSnap>().AddRange(
            // Only baseline has MLScoring/quality_gate_fail (500 rows) — never in latest.
            Snap(1, dec, "MLScoring", "quality_gate_fail", 200),
            Snap(2, jan, "MLScoring", "quality_gate_fail", 150),
            Snap(3, feb, "MLScoring", "quality_gate_fail", 150),
            Snap(4, mar, "Regime",    "blocked",           100));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(new GetCalibrationTrendReportQuery(), CancellationToken.None);

        var mlRow = resp.data!.Rows.Single(r => r.Stage == "MLScoring");
        Assert.Equal(0,     mlRow.LatestMonthCount);
        Assert.Equal(500,   mlRow.BaselineCount);
        Assert.True(mlRow.IsAnomaly);           // 100% → 0% swing (-1.0) on 500 baseline
        Assert.Contains("falling", mlRow.Hint!);
    }

    [Fact]
    public async Task Handler_Clamps_Baseline_Months_To_Minimum_One()
    {
        using var ctx = NewCtx();
        var mar = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc);
        var feb = new DateTime(2026, 02, 01, 0, 0, 0, DateTimeKind.Utc);

        ctx.Set<DomainSnap>().AddRange(
            Snap(1, feb, "Regime", "blocked", 100),
            Snap(2, mar, "Regime", "blocked", 100));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(
            new GetCalibrationTrendReportQuery { BaselineMonths = 0 }, CancellationToken.None);

        Assert.Equal(feb, resp.data!.BaselineStart);
        Assert.Equal(mar, resp.data.BaselineEnd);
    }
}
