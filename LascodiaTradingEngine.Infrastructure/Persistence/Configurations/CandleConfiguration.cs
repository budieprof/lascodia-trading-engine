using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

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
        builder.Property(x => x.Volume).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        // Composite index for efficient candle lookups
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Timestamp }).IsUnique();
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.IsClosed });
    }
}
