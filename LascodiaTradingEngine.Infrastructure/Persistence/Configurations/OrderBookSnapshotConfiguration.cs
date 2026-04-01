using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class OrderBookSnapshotConfiguration : IEntityTypeConfiguration<OrderBookSnapshot>
{
    public void Configure(EntityTypeBuilder<OrderBookSnapshot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.InstanceId).IsRequired().HasMaxLength(100);

        builder.Property(x => x.BidPrice).HasPrecision(18, 8);
        builder.Property(x => x.AskPrice).HasPrecision(18, 8);
        builder.Property(x => x.BidVolume).HasPrecision(18, 5);
        builder.Property(x => x.AskVolume).HasPrecision(18, 5);
        builder.Property(x => x.SpreadPoints).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.CapturedAt });

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
