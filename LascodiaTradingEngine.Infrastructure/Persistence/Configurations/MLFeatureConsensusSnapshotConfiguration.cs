using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLFeatureConsensusSnapshot"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLFeatureConsensusSnapshotConfiguration : IEntityTypeConfiguration<MLFeatureConsensusSnapshot>
{
    public void Configure(EntityTypeBuilder<MLFeatureConsensusSnapshot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.FeatureConsensusJson).HasColumnType("text");
        builder.Property(x => x.SchemaKey).IsRequired().HasMaxLength(256).HasDefaultValue("legacy");
        builder.Property(x => x.ImportanceSourceSummaryJson).HasColumnType("text").HasDefaultValue("{}");
        builder.Property(x => x.ContributorModelIdsJson).HasColumnType("text").HasDefaultValue("[]");

        // One consensus snapshot per symbol/timeframe at a time; older rows are retained for history
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.DetectedAt });
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.SchemaKey, x.DetectedAt });

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
