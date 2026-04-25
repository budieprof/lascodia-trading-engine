using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLAdwinDriftLog"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLAdwinDriftLogConfiguration : IEntityTypeConfiguration<MLAdwinDriftLog>
{
    public void Configure(EntityTypeBuilder<MLAdwinDriftLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Window1Mean).HasPrecision(18, 8);
        builder.Property(x => x.Window2Mean).HasPrecision(18, 8);
        builder.Property(x => x.EpsilonCut).HasPrecision(18, 8);
        builder.Property(x => x.AccuracyDrop).HasPrecision(18, 8);
        builder.Property(x => x.DeltaUsed).HasPrecision(10, 8);
        builder.Property(x => x.DominantRegime).HasConversion<string>().HasMaxLength(20);
        builder.HasOne(x => x.MLModel)
               .WithMany()
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasQueryFilter(x => !x.IsDeleted);
        builder.HasIndex(x => new { x.MLModelId, x.DetectedAt });
        builder.HasIndex(x => x.DetectedAt);
    }
}
