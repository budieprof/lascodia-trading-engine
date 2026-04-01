using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class TickRecordConfiguration : IEntityTypeConfiguration<TickRecord>
{
    public void Configure(EntityTypeBuilder<TickRecord> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.InstanceId).IsRequired().HasMaxLength(100);

        builder.Property(x => x.Bid).HasPrecision(18, 8);
        builder.Property(x => x.Ask).HasPrecision(18, 8);
        builder.Property(x => x.Mid).HasPrecision(18, 8);
        builder.Property(x => x.SpreadPoints).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.TickTimestamp });

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
