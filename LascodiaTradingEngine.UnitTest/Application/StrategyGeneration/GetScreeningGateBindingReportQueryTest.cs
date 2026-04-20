using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Application.StrategyGeneration.Queries.GetScreeningGateBindingReport;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.Application.Workers;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyGeneration;

/// <summary>
/// Covers the binding-gate diagnostic: aggregation, dominance classification,
/// infrastructure-reason exclusion, sample-size reliability floor, and
/// recommendation dispatch.
/// </summary>
public class GetScreeningGateBindingReportQueryTest
{
    private readonly DateTime _now = new(2026, 04, 20, 0, 0, 0, DateTimeKind.Utc);

    private BindingReportTestContext NewCtx()
    {
        var opts = new DbContextOptionsBuilder<BindingReportTestContext>()
            .UseInMemoryDatabase($"binding-{Guid.NewGuid()}").Options;
        return new BindingReportTestContext(opts);
    }

    private GetScreeningGateBindingReportQueryHandler NewHandler(IReadApplicationDbContext ctx)
        => new(ctx, new FixedTimeProvider(_now));

    private StrategyGenerationFailure Fail(long id, ScreeningFailureReason reason, StrategyType type, DateTime at) =>
        new()
        {
            Id            = id,
            CandidateId   = $"c-{id}",
            CandidateHash = "h",
            StrategyType  = type,
            Symbol        = "EURUSD",
            Timeframe     = Timeframe.H1,
            FailureStage  = "Screening",
            FailureReason = reason.ToString(),
            CreatedAtUtc  = at,
        };

    [Fact]
    public async Task Handler_Returns_Empty_Report_When_No_Failures()
    {
        using var ctx = NewCtx();

        var resp = await NewHandler(ctx).Handle(new GetScreeningGateBindingReportQuery(), CancellationToken.None);

        Assert.True(resp.status);
        Assert.Equal(0, resp.data!.TotalFailures);
        Assert.False(resp.data.IsReliable);
        Assert.Null(resp.data.BindingReason);
        Assert.Null(resp.data.Recommendation);
        Assert.Empty(resp.data.Rows);
    }

    [Fact]
    public async Task Handler_Filters_By_Lookback_Window()
    {
        using var ctx = NewCtx();
        // Two rows inside the window, one outside.
        ctx.Set<StrategyGenerationFailure>().AddRange(
            Fail(1, ScreeningFailureReason.EquityCurveR2, StrategyType.MovingAverageCrossover, _now.AddDays(-5)),
            Fail(2, ScreeningFailureReason.EquityCurveR2, StrategyType.MovingAverageCrossover, _now.AddDays(-29)),
            Fail(3, ScreeningFailureReason.EquityCurveR2, StrategyType.MovingAverageCrossover, _now.AddDays(-31)));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(new GetScreeningGateBindingReportQuery { LookbackDays = 30, MinBindingCount = 1 }, CancellationToken.None);

        Assert.Equal(2, resp.data!.TotalFailures);
    }

    [Fact]
    public async Task Handler_Identifies_Binding_Gate_And_Recommendation()
    {
        using var ctx = NewCtx();
        // 60 rows: 40 EquityCurveR2 (binding), 20 Degradation. EquityCurveR2 is
        // Underfit-class, so the overall class = Underfit at 0.55 dominance.
        for (int i = 0; i < 40; i++)
            ctx.Set<StrategyGenerationFailure>().Add(
                Fail(i + 1, ScreeningFailureReason.EquityCurveR2, StrategyType.MovingAverageCrossover, _now.AddDays(-1)));
        for (int i = 0; i < 20; i++)
            ctx.Set<StrategyGenerationFailure>().Add(
                Fail(i + 100, ScreeningFailureReason.Degradation, StrategyType.RSIReversion, _now.AddDays(-1)));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(new GetScreeningGateBindingReportQuery(), CancellationToken.None);

        Assert.True(resp.data!.IsReliable);
        Assert.Equal(60, resp.data.TotalFailures);
        Assert.Equal(nameof(ScreeningFailureReason.EquityCurveR2), resp.data.BindingReason);
        Assert.Equal(40m / 60m, resp.data.BindingReasonShare);
        Assert.Equal(nameof(RejectionClass.Underfit), resp.data.BindingClass);
        Assert.Equal(nameof(RejectionClass.Underfit), resp.data.OverallClass);
        Assert.NotNull(resp.data.Recommendation);
        Assert.Contains("MinEquityCurveR", resp.data.Recommendation!);
    }

    [Fact]
    public async Task Handler_Excludes_Infrastructure_Reasons_From_Binding_Decision()
    {
        using var ctx = NewCtx();
        // Timeout dominates by count, but it's infrastructure — the binding
        // gate should be the #2 tunable reason (EquityCurveR2).
        for (int i = 0; i < 80; i++)
            ctx.Set<StrategyGenerationFailure>().Add(
                Fail(i + 1, ScreeningFailureReason.Timeout, StrategyType.MovingAverageCrossover, _now.AddDays(-1)));
        for (int i = 0; i < 30; i++)
            ctx.Set<StrategyGenerationFailure>().Add(
                Fail(i + 200, ScreeningFailureReason.EquityCurveR2, StrategyType.MovingAverageCrossover, _now.AddDays(-1)));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(new GetScreeningGateBindingReportQuery(), CancellationToken.None);

        Assert.Equal(nameof(ScreeningFailureReason.EquityCurveR2), resp.data!.BindingReason);
        // The Timeout row is still present in rows (for visibility), but not binding.
        Assert.Contains(resp.data.Rows, r => r.Reason == nameof(ScreeningFailureReason.Timeout));
    }

    [Fact]
    public async Task Handler_Marks_Unreliable_When_Sample_Below_Floor()
    {
        using var ctx = NewCtx();
        // 10 rows, default MinBindingCount = 50 → not reliable.
        for (int i = 0; i < 10; i++)
            ctx.Set<StrategyGenerationFailure>().Add(
                Fail(i + 1, ScreeningFailureReason.EquityCurveR2, StrategyType.MovingAverageCrossover, _now.AddDays(-1)));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(new GetScreeningGateBindingReportQuery(), CancellationToken.None);

        Assert.False(resp.data!.IsReliable);
        Assert.Null(resp.data.Recommendation);
        Assert.Equal(10, resp.data.TotalFailures); // rows still returned for visibility
    }

    [Fact]
    public async Task Handler_Classifies_Overall_As_Overfit_When_Overfit_Dominates()
    {
        using var ctx = NewCtx();
        // 40 overfit, 10 underfit → overfit share 0.8 > 0.55 → Overfit.
        for (int i = 0; i < 40; i++)
            ctx.Set<StrategyGenerationFailure>().Add(
                Fail(i + 1, ScreeningFailureReason.Degradation, StrategyType.MovingAverageCrossover, _now.AddDays(-1)));
        for (int i = 0; i < 10; i++)
            ctx.Set<StrategyGenerationFailure>().Add(
                Fail(i + 200, ScreeningFailureReason.EquityCurveR2, StrategyType.MovingAverageCrossover, _now.AddDays(-1)));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(new GetScreeningGateBindingReportQuery(), CancellationToken.None);

        Assert.Equal(nameof(RejectionClass.Overfit), resp.data!.OverallClass);
        Assert.Equal(nameof(ScreeningFailureReason.Degradation), resp.data.BindingReason);
        Assert.Contains("overfit", resp.data.Recommendation!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handler_Classifies_Overall_As_Mixed_When_Neither_Class_Dominates()
    {
        using var ctx = NewCtx();
        // 25 underfit, 25 overfit → neither reaches 0.55 → Mixed.
        for (int i = 0; i < 25; i++)
            ctx.Set<StrategyGenerationFailure>().Add(
                Fail(i + 1, ScreeningFailureReason.EquityCurveR2, StrategyType.MovingAverageCrossover, _now.AddDays(-1)));
        for (int i = 0; i < 25; i++)
            ctx.Set<StrategyGenerationFailure>().Add(
                Fail(i + 200, ScreeningFailureReason.Degradation, StrategyType.RSIReversion, _now.AddDays(-1)));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(new GetScreeningGateBindingReportQuery(), CancellationToken.None);

        Assert.Equal(nameof(RejectionClass.Mixed), resp.data!.OverallClass);
    }

    [Fact]
    public async Task Handler_Reports_TopStrategyType_Per_Row()
    {
        using var ctx = NewCtx();
        // EquityCurveR2: 30 for MovingAverageCrossover, 10 for RSIReversion → top = MovingAverageCrossover.
        for (int i = 0; i < 30; i++)
            ctx.Set<StrategyGenerationFailure>().Add(
                Fail(i + 1, ScreeningFailureReason.EquityCurveR2, StrategyType.MovingAverageCrossover, _now.AddDays(-1)));
        for (int i = 0; i < 10; i++)
            ctx.Set<StrategyGenerationFailure>().Add(
                Fail(i + 200, ScreeningFailureReason.EquityCurveR2, StrategyType.RSIReversion, _now.AddDays(-1)));
        ctx.SaveChanges();

        var resp = await NewHandler(ctx).Handle(new GetScreeningGateBindingReportQuery { MinBindingCount = 1 }, CancellationToken.None);

        var row = resp.data!.Rows.Single();
        Assert.Equal(nameof(StrategyType.MovingAverageCrossover), row.TopStrategyType);
        Assert.Equal(30, row.TopStrategyTypeCount);
    }

    [Fact]
    public void RecommendForReason_Covers_Every_Tunable_Reason()
    {
        foreach (var reason in Enum.GetValues<ScreeningFailureReason>())
        {
            if (reason == ScreeningFailureReason.None
             || reason == ScreeningFailureReason.Timeout
             || reason == ScreeningFailureReason.TaskFault)
                continue;

            var hint = GetScreeningGateBindingReportQueryHandler.RecommendForReason(reason.ToString());
            Assert.False(string.IsNullOrWhiteSpace(hint),
                $"Missing recommendation for {reason}");
            Assert.DoesNotContain("No specific recommendation", hint);
        }
    }
}

internal sealed class BindingReportTestContext : DbContext, IReadApplicationDbContext
{
    public BindingReportTestContext(DbContextOptions<BindingReportTestContext> options) : base(options) { }

    public DbContext GetDbContext() => this;

    public new int SaveChanges() => base.SaveChanges();

    public new Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => base.SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StrategyGenerationFailure>().HasKey(e => e.Id);
    }
}
