using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="AccountPerformanceAttribution"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class AccountPerformanceAttributionConfiguration : IEntityTypeConfiguration<AccountPerformanceAttribution>
{
    public void Configure(EntityTypeBuilder<AccountPerformanceAttribution> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.StrategyAttributionJson).IsRequired().HasMaxLength(8000);
        builder.Property(x => x.SymbolAttributionJson).IsRequired().HasMaxLength(8000);
        builder.Property(x => x.Granularity).HasConversion<int>();

        builder.HasOne(x => x.TradingAccount)
            .WithMany()
            .HasForeignKey(x => x.TradingAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.StartOfDayEquity).HasPrecision(18, 2);
        builder.Property(x => x.EndOfDayEquity).HasPrecision(18, 2);
        builder.Property(x => x.RealizedPnl).HasPrecision(18, 2);
        builder.Property(x => x.UnrealizedPnlChange).HasPrecision(18, 2);
        builder.Property(x => x.DailyReturnPct).HasPrecision(18, 4);
        builder.Property(x => x.MLAlphaPnl).HasPrecision(18, 2);
        builder.Property(x => x.TimingAlphaPnl).HasPrecision(18, 2);
        builder.Property(x => x.ExecutionCosts).HasPrecision(18, 2);
        builder.Property(x => x.SharpeRatio7d).HasPrecision(18, 8);
        builder.Property(x => x.SharpeRatio30d).HasPrecision(18, 8);
        builder.Property(x => x.SortinoRatio30d).HasPrecision(18, 8);
        builder.Property(x => x.CalmarRatio30d).HasPrecision(18, 8);
        builder.Property(x => x.BenchmarkReturnPct).HasPrecision(18, 4);
        builder.Property(x => x.AlphaVsBenchmarkPct).HasPrecision(18, 4);
        builder.Property(x => x.ActiveReturnPct).HasPrecision(18, 4);
        builder.Property(x => x.InformationRatio).HasPrecision(18, 8);
        builder.Property(x => x.GrossAlphaPct).HasPrecision(18, 4);
        builder.Property(x => x.ExecutionCostPct).HasPrecision(18, 4);
        builder.Property(x => x.NetAlphaPct).HasPrecision(18, 4);
        builder.Property(x => x.WinRate).HasPrecision(18, 4);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.TradingAccountId, x.AttributionDate, x.Granularity }).IsUnique();

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
