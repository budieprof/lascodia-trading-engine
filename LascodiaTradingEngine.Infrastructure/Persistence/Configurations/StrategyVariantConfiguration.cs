using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="StrategyVariant"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class StrategyVariantConfiguration : IEntityTypeConfiguration<StrategyVariant>
{
    public void Configure(EntityTypeBuilder<StrategyVariant> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.ParameterOverridesJson).IsRequired().HasMaxLength(4000);
        builder.Property(x => x.ComparisonResultJson).HasMaxLength(4000);

        builder.HasOne(x => x.BaseStrategy)
            .WithMany()
            .HasForeignKey(x => x.BaseStrategyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.ShadowWinRate).HasPrecision(18, 4);
        builder.Property(x => x.ShadowExpectedValue).HasPrecision(18, 8);
        builder.Property(x => x.ShadowSharpeRatio).HasPrecision(18, 8);
        builder.Property(x => x.BaseWinRate).HasPrecision(18, 4);
        builder.Property(x => x.BaseExpectedValue).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.BaseStrategyId, x.IsActive });

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
