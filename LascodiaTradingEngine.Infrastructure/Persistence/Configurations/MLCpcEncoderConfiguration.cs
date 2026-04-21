using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLCpcEncoder"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLCpcEncoderConfiguration : IEntityTypeConfiguration<MLCpcEncoder>
{
    public void Configure(EntityTypeBuilder<MLCpcEncoder> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.InfoNceLoss).HasPrecision(12, 6);
        builder.HasQueryFilter(x => !x.IsDeleted);
        // Lookup path for V7 projection, including architecture-aware retraining decisions.
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Regime, x.EncoderType, x.IsActive });

        // Production safety rail: only one live encoder may be served for a
        // (symbol, timeframe, regime) triple. `AreNullsDistinct(false)` makes PostgreSQL
        // treat the null global-regime row as a real key value for uniqueness.
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Regime })
            .IsUnique()
            .HasFilter("\"IsActive\" = true AND \"IsDeleted\" = false")
            .AreNullsDistinct(false);
    }
}
