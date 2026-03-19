using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class MarketRegimeSnapshotConfiguration : IEntityTypeConfiguration<MarketRegimeSnapshot>
{
    public void Configure(EntityTypeBuilder<MarketRegimeSnapshot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.Regime).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.Confidence).HasPrecision(5, 4);
        builder.Property(x => x.ADX).HasPrecision(10, 4);
        builder.Property(x => x.ATR).HasPrecision(18, 6);
        builder.Property(x => x.BollingerBandWidth).HasPrecision(18, 6);

        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.DetectedAt });
    }
}
