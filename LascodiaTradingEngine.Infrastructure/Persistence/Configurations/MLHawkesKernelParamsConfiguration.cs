using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLHawkesKernelParams"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLHawkesKernelParamsConfiguration : IEntityTypeConfiguration<MLHawkesKernelParams>
{
    public void Configure(EntityTypeBuilder<MLHawkesKernelParams> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.Mu).HasPrecision(18, 8);
        builder.Property(x => x.Alpha).HasPrecision(18, 8);
        builder.Property(x => x.Beta).HasPrecision(18, 8);
        builder.Property(x => x.LogLikelihood).HasPrecision(18, 4);
        builder.Property(x => x.SuppressMultiplier).HasPrecision(5, 2);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.FittedAt });
        builder.HasIndex(x => new { x.Symbol, x.Timeframe })
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");
    }
}
