using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLModelVolatilityAccuracy"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLModelVolatilityAccuracyConfiguration : IEntityTypeConfiguration<MLModelVolatilityAccuracy>
{
    public void Configure(EntityTypeBuilder<MLModelVolatilityAccuracy> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.VolatilityBucket).IsRequired().HasMaxLength(20);

        builder.Property(x => x.Accuracy).HasColumnType("double precision");
        builder.Property(x => x.AtrThresholdLow).HasColumnType("numeric");
        builder.Property(x => x.AtrThresholdHigh).HasColumnType("numeric");

        // One row per (model, bucket) — upserted on each compute cycle.
        builder.HasIndex(x => new { x.MLModelId, x.VolatilityBucket }).IsUnique();
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.VolatilityBucket });

        builder.HasOne(x => x.MLModel)
               .WithMany()
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
