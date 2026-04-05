using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MarketDataAnomaly"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MarketDataAnomalyConfiguration : IEntityTypeConfiguration<MarketDataAnomaly>
{
    public void Configure(EntityTypeBuilder<MarketDataAnomaly> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.AnomalyType).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.InstanceId).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Description).IsRequired().HasMaxLength(1000);

        builder.Property(x => x.AnomalousValue).HasPrecision(18, 8);
        builder.Property(x => x.ExpectedValue).HasPrecision(18, 8);
        builder.Property(x => x.DeviationMagnitude).HasPrecision(18, 8);
        builder.Property(x => x.LastGoodBid).HasPrecision(18, 8);
        builder.Property(x => x.LastGoodAsk).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.DetectedAt });

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
