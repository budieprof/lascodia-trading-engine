using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="Candle"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class CandleConfiguration : IEntityTypeConfiguration<Candle>
{
    public void Configure(EntityTypeBuilder<Candle> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(5);
        builder.Property(x => x.Open).HasPrecision(18, 8);
        builder.Property(x => x.High).HasPrecision(18, 8);
        builder.Property(x => x.Low).HasPrecision(18, 8);
        builder.Property(x => x.Close).HasPrecision(18, 8);
        // Volume precision raised from 18,8 to 28,8: the prior cap (~10^10 ≈ 10B) is
        // exceeded by aggregated tick-volume rows on heavy D1 candles for real-volume
        // symbols (indices / metals), causing Postgres 22003 overflow on insert. 28,8
        // gives ~10^20 headroom — above int64 max so a pathological broker tick still fits.
        builder.Property(x => x.Volume).HasPrecision(28, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        // Composite index for efficient candle lookups
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Timestamp }).IsUnique();
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.IsClosed });
        builder.HasIndex(x => new { x.Timestamp, x.Id })
            .HasDatabaseName("IX_Candle_ClosedOldestScan")
            .HasFilter("\"IsClosed\" = true AND \"IsDeleted\" = false");
    }
}
