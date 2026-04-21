using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLFeatureInteractionAudit"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLFeatureInteractionAuditConfiguration : IEntityTypeConfiguration<MLFeatureInteractionAudit>
{
    public void Configure(EntityTypeBuilder<MLFeatureInteractionAudit> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.FeatureNameA).IsRequired().HasMaxLength(50);
        builder.Property(x => x.FeatureNameB).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Method).IsRequired().HasMaxLength(80);
        builder.Property(x => x.InteractionScore).HasPrecision(18, 8);
        builder.Property(x => x.EffectSize).HasPrecision(18, 8);
        builder.Property(x => x.PValue).HasPrecision(18, 8);
        builder.Property(x => x.QValue).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.MLModelId, x.Rank });
        builder.HasIndex(x => new { x.MLModelId, x.IsIncludedAsFeature });
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.IsIncludedAsFeature, x.ComputedAt });

        builder.HasOne(x => x.MLModel)
               .WithMany(x => x.FeatureInteractionAudits)
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
