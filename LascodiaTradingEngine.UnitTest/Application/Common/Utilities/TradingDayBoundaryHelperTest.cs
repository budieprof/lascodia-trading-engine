using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Common.Utilities;

public sealed class TradingDayBoundaryHelperTest
{
    [Fact]
    public void GetTradingDayStartUtc_RespectsConfiguredRolloverMinute()
    {
        var beforeRollover = new DateTime(2026, 4, 24, 21, 30, 0, DateTimeKind.Utc);
        var afterRollover = new DateTime(2026, 4, 24, 22, 30, 0, DateTimeKind.Utc);

        var beforeStart = TradingDayBoundaryHelper.GetTradingDayStartUtc(beforeRollover, 22 * 60);
        var afterStart = TradingDayBoundaryHelper.GetTradingDayStartUtc(afterRollover, 22 * 60);

        Assert.Equal(new DateTime(2026, 4, 23, 22, 0, 0, DateTimeKind.Utc), beforeStart);
        Assert.Equal(new DateTime(2026, 4, 24, 22, 0, 0, DateTimeKind.Utc), afterStart);
    }

    [Fact]
    public async Task ResolveStartOfDayEquityAsync_UsesFirstAttributionInsideTradingDay()
    {
        await using var db = CreateDbContext();
        db.Add(new AccountPerformanceAttribution
        {
            Id = 1,
            TradingAccountId = 7,
            AttributionDate = new DateTime(2026, 4, 24, 22, 15, 0, DateTimeKind.Utc),
            StartOfDayEquity = 10000m,
            EndOfDayEquity = 10020m,
            IsDeleted = false
        });
        await db.SaveChangesAsync();

        var baseline = await TradingDayBoundaryHelper.ResolveStartOfDayEquityAsync(
            db,
            7,
            new DateTime(2026, 4, 24, 23, 00, 0, DateTimeKind.Utc),
            new TradingDayOptions { RolloverMinuteOfDayUtc = 22 * 60 },
            CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.Equal(new DateTime(2026, 4, 24, 22, 0, 0, DateTimeKind.Utc), baseline!.TradingDayStartUtc);
        Assert.Equal(10000m, baseline!.StartOfDayEquity);
        Assert.StartsWith("Attribution:", baseline.Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveStartOfDayEquityAsync_UsesPreviousAttribution_WhenCurrentTradingDayRecordMissing()
    {
        await using var db = CreateDbContext();
        db.Add(new AccountPerformanceAttribution
        {
            Id = 1,
            TradingAccountId = 7,
            AttributionDate = new DateTime(2026, 4, 23, 21, 0, 0, DateTimeKind.Utc),
            StartOfDayEquity = 9800m,
            EndOfDayEquity = 9950m,
            IsDeleted = false
        });
        await db.SaveChangesAsync();

        var baseline = await TradingDayBoundaryHelper.ResolveStartOfDayEquityAsync(
            db,
            7,
            new DateTime(2026, 4, 24, 23, 00, 0, DateTimeKind.Utc),
            new TradingDayOptions { RolloverMinuteOfDayUtc = 22 * 60 },
            CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.Equal(new DateTime(2026, 4, 24, 22, 0, 0, DateTimeKind.Utc), baseline!.TradingDayStartUtc);
        Assert.Equal(9950m, baseline!.StartOfDayEquity);
        Assert.StartsWith("PreviousAttribution:", baseline.Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveStartOfDayEquityAsync_UsesNearestBrokerSnapshotWithinTolerance()
    {
        await using var db = CreateDbContext();
        db.Add(new BrokerAccountSnapshot
        {
            Id = 1,
            TradingAccountId = 7,
            InstanceId = "EA-001",
            Balance = 10000m,
            Equity = 10025m,
            MarginUsed = 0m,
            FreeMargin = 10025m,
            Currency = "USD",
            ReportedAt = new DateTime(2026, 4, 24, 21, 58, 0, DateTimeKind.Utc),
            IsDeleted = false
        });
        db.Add(new BrokerAccountSnapshot
        {
            Id = 2,
            TradingAccountId = 7,
            InstanceId = "EA-001",
            Balance = 10000m,
            Equity = 9950m,
            MarginUsed = 0m,
            FreeMargin = 9950m,
            Currency = "USD",
            ReportedAt = new DateTime(2026, 4, 24, 22, 45, 0, DateTimeKind.Utc),
            IsDeleted = false
        });
        await db.SaveChangesAsync();

        var baseline = await TradingDayBoundaryHelper.ResolveStartOfDayEquityAsync(
            db,
            7,
            new DateTime(2026, 4, 24, 23, 00, 0, DateTimeKind.Utc),
            new TradingDayOptions
            {
                RolloverMinuteOfDayUtc = 22 * 60,
                BrokerSnapshotBoundaryToleranceMinutes = 30
            },
            CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.Equal(new DateTime(2026, 4, 24, 22, 0, 0, DateTimeKind.Utc), baseline!.TradingDayStartUtc);
        Assert.Equal(10025m, baseline!.StartOfDayEquity);
        Assert.StartsWith("BrokerSnapshot:", baseline.Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveStartOfDayEquityAsync_IgnoresBrokerSnapshotsForOtherAccounts()
    {
        await using var db = CreateDbContext();
        db.Add(new BrokerAccountSnapshot
        {
            Id = 1,
            TradingAccountId = 99,
            InstanceId = "EA-001",
            Balance = 10000m,
            Equity = 10025m,
            MarginUsed = 0m,
            FreeMargin = 10025m,
            Currency = "USD",
            ReportedAt = new DateTime(2026, 4, 24, 21, 58, 0, DateTimeKind.Utc),
            IsDeleted = false
        });
        await db.SaveChangesAsync();

        var baseline = await TradingDayBoundaryHelper.ResolveStartOfDayEquityAsync(
            db,
            7,
            new DateTime(2026, 4, 24, 23, 00, 0, DateTimeKind.Utc),
            new TradingDayOptions
            {
                RolloverMinuteOfDayUtc = 22 * 60,
                BrokerSnapshotBoundaryToleranceMinutes = 30
            },
            CancellationToken.None);

        Assert.Null(baseline);
    }

    [Fact]
    public async Task ResolveStartOfDayEquityAsync_IgnoresBrokerSnapshotOutsideTolerance()
    {
        await using var db = CreateDbContext();
        db.Add(new BrokerAccountSnapshot
        {
            Id = 1,
            TradingAccountId = 7,
            InstanceId = "EA-001",
            Balance = 10000m,
            Equity = 10025m,
            MarginUsed = 0m,
            FreeMargin = 10025m,
            Currency = "USD",
            ReportedAt = new DateTime(2026, 4, 24, 18, 0, 0, DateTimeKind.Utc),
            IsDeleted = false
        });
        await db.SaveChangesAsync();

        var baseline = await TradingDayBoundaryHelper.ResolveStartOfDayEquityAsync(
            db,
            7,
            new DateTime(2026, 4, 24, 23, 00, 0, DateTimeKind.Utc),
            new TradingDayOptions
            {
                RolloverMinuteOfDayUtc = 22 * 60,
                BrokerSnapshotBoundaryToleranceMinutes = 30
            },
            CancellationToken.None);

        Assert.Null(baseline);
    }

    private static TestDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountPerformanceAttribution>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
                builder.Ignore(x => x.TradingAccount);
            });

            modelBuilder.Entity<BrokerAccountSnapshot>(builder =>
            {
                builder.HasKey(x => x.Id);
                builder.HasQueryFilter(x => !x.IsDeleted);
            });
        }
    }
}
