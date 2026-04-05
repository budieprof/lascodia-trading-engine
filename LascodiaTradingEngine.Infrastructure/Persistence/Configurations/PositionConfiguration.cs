using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="Position"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    public void Configure(EntityTypeBuilder<Position> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.BrokerPositionId).HasMaxLength(200);

        builder.Property(x => x.OpenLots)
            .HasPrecision(18, 8);

        builder.Property(x => x.AverageEntryPrice)
            .HasPrecision(18, 8);

        builder.Property(x => x.CurrentPrice)
            .HasPrecision(18, 8);

        builder.Property(x => x.UnrealizedPnL)
            .HasPrecision(18, 8);

        builder.Property(x => x.RealizedPnL)
            .HasPrecision(18, 8);

        builder.Property(x => x.StopLoss)
            .HasPrecision(18, 8);

        builder.Property(x => x.TakeProfit)
            .HasPrecision(18, 8);

        builder.Property(x => x.TrailingStopLevel)
            .HasPrecision(18, 8);

        builder.Property(x => x.Direction)
            .HasConversion<string>()
            .HasMaxLength(10);

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(x => x.TrailingStopType)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(x => x.TrailingStopValue)
            .HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.OpenOrderId).IsUnique().HasFilter("\"OpenOrderId\" IS NOT NULL");
        builder.HasIndex(x => new { x.Symbol, x.Status });
    }
}
