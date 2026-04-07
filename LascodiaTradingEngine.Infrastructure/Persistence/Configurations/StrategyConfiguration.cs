using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="Strategy"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class StrategyConfiguration : IEntityTypeConfiguration<Strategy>
{
    public void Configure(EntityTypeBuilder<Strategy> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.Symbol)
            .HasMaxLength(10);

        builder.Property(x => x.Timeframe)
            .HasConversion<string>()
            .HasMaxLength(5);

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(x => x.StrategyType)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(x => x.LifecycleStage)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(x => x.EstimatedCapacityLots).HasPrecision(18, 2);

        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.Symbol);
        builder.HasIndex(x => x.RiskProfileId);

        builder.HasOne(x => x.RiskProfile)
               .WithMany(x => x.Strategies)
               .HasForeignKey(x => x.RiskProfileId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}
