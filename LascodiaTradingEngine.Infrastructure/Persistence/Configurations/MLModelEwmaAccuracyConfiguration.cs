using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLModelEwmaAccuracy"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLModelEwmaAccuracyConfiguration : IEntityTypeConfiguration<MLModelEwmaAccuracy>
{
    public void Configure(EntityTypeBuilder<MLModelEwmaAccuracy> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.EwmaAccuracy).HasColumnType("double precision");
        builder.Property(x => x.Alpha).HasColumnType("double precision");

        // One row per active model — upserted on each compute cycle.
        builder.HasIndex(x => new { x.MLModelId }).IsUnique();
        builder.HasIndex(x => new { x.Symbol, x.Timeframe });

        builder.HasOne(x => x.MLModel)
               .WithMany()
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
