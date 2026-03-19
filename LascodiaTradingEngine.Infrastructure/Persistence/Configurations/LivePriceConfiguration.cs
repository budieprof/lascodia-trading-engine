using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class LivePriceConfiguration : IEntityTypeConfiguration<LivePrice>
{
    public void Configure(EntityTypeBuilder<LivePrice> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Bid).HasPrecision(18, 8);
        builder.Property(x => x.Ask).HasPrecision(18, 8);

        // One row per symbol — enforced at the DB level.
        builder.HasIndex(x => x.Symbol).IsUnique();
    }
}
