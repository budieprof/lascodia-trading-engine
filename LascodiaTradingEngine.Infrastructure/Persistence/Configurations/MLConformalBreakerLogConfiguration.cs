using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLConformalBreakerLog"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLConformalBreakerLogConfiguration : IEntityTypeConfiguration<MLConformalBreakerLog>
{
    public void Configure(EntityTypeBuilder<MLConformalBreakerLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.TripReason).HasConversion<string>().IsRequired().HasMaxLength(40);

        builder.Property(x => x.EmpiricalCoverage).HasPrecision(18, 8);
        builder.Property(x => x.TargetCoverage).HasPrecision(5, 4);
        builder.Property(x => x.CoverageThreshold).HasPrecision(10, 8);
        builder.Property(x => x.CoverageLowerBound).HasPrecision(18, 8);
        builder.Property(x => x.CoverageUpperBound).HasPrecision(18, 8);
        builder.Property(x => x.CoveragePValue).HasPrecision(18, 12);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.MLModelId);
        builder.HasIndex(x => new { x.MLModelId, x.Symbol, x.Timeframe })
               .IsUnique()
               .HasFilter("\"IsActive\" = TRUE AND \"IsDeleted\" = FALSE");

        builder.HasOne(x => x.MLModel)
               .WithMany()
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
