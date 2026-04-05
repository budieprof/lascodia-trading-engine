using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="StrategyPerformanceSnapshot"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class StrategyPerformanceSnapshotConfiguration : IEntityTypeConfiguration<StrategyPerformanceSnapshot>
{
    public void Configure(EntityTypeBuilder<StrategyPerformanceSnapshot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.HealthStatus).HasConversion<string>().IsRequired().HasMaxLength(20);

        builder.Property(x => x.WinRate).HasPrecision(5, 4);
        builder.Property(x => x.ProfitFactor).HasPrecision(18, 4);
        builder.Property(x => x.SharpeRatio).HasPrecision(18, 4);
        builder.Property(x => x.MaxDrawdownPct).HasPrecision(18, 4);
        builder.Property(x => x.TotalPnL).HasPrecision(18, 8);
        builder.Property(x => x.HealthScore).HasPrecision(5, 4);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.StrategyId);
        builder.HasIndex(x => x.EvaluatedAt);

        builder.HasOne(x => x.Strategy)
               .WithMany(x => x.PerformanceSnapshots)
               .HasForeignKey(x => x.StrategyId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
