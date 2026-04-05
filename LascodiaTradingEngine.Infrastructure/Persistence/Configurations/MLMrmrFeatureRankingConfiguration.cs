using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLMrmrFeatureRanking"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLMrmrFeatureRankingConfiguration : IEntityTypeConfiguration<MLMrmrFeatureRanking>
{
    public void Configure(EntityTypeBuilder<MLMrmrFeatureRanking> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.FeatureName).IsRequired().HasMaxLength(64);
        builder.Property(x => x.MutualInfoWithTarget).HasPrecision(10, 8);
        builder.Property(x => x.RedundancyScore).HasPrecision(10, 8);
        builder.Property(x => x.MrmrScore).HasPrecision(10, 8);
        builder.HasQueryFilter(x => !x.IsDeleted);
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.MrmrRank });
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.ComputedAt });
    }
}
