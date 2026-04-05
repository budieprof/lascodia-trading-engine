using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLModelSessionAccuracy"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLModelSessionAccuracyConfiguration : IEntityTypeConfiguration<MLModelSessionAccuracy>
{
    public void Configure(EntityTypeBuilder<MLModelSessionAccuracy> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.Session).HasConversion<string>().IsRequired().HasMaxLength(20);

        builder.Property(x => x.Accuracy).HasColumnType("double precision");

        // One row per (model, session) — upserted on each compute cycle.
        builder.HasIndex(x => new { x.MLModelId, x.Session }).IsUnique();
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Session });

        builder.HasOne(x => x.MLModel)
               .WithMany()
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
