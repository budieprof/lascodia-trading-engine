using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLModelHourlyAccuracy"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLModelHourlyAccuracyConfiguration : IEntityTypeConfiguration<MLModelHourlyAccuracy>
{
    public void Configure(EntityTypeBuilder<MLModelHourlyAccuracy> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.Accuracy).HasColumnType("double precision");

        // One row per (model, hour) — upserted on each compute cycle.
        builder.HasIndex(x => new { x.MLModelId, x.HourUtc }).IsUnique();
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.HourUtc });

        builder.HasOne(x => x.MLModel)
               .WithMany()
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
