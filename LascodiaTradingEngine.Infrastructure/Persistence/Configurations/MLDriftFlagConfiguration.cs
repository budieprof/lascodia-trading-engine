using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLDriftFlag"/>. Enforces a unique
/// (Symbol, Timeframe, DetectorType) tuple so each drift detector has at most one
/// live flag per pair, and indexes on (DetectorType, ExpiresAtUtc) so workers can
/// efficiently sweep active flags.
/// </summary>
public class MLDriftFlagConfiguration : IEntityTypeConfiguration<MLDriftFlag>
{
    public void Configure(EntityTypeBuilder<MLDriftFlag> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.DetectorType).IsRequired().HasMaxLength(40);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.DetectorType })
               .IsUnique();
        builder.HasIndex(x => new { x.DetectorType, x.ExpiresAtUtc });
    }
}
