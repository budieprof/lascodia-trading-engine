using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class EconomicEventConfiguration : IEntityTypeConfiguration<EconomicEvent>
{
    public void Configure(EntityTypeBuilder<EconomicEvent> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Title).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Currency).IsRequired().HasMaxLength(3);
        builder.Property(x => x.Impact).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.Source).HasConversion<string>().IsRequired().HasMaxLength(50);
        builder.Property(x => x.Forecast).HasMaxLength(50);
        builder.Property(x => x.Previous).HasMaxLength(50);
        builder.Property(x => x.Actual).HasMaxLength(50);

        builder.HasIndex(x => new { x.Currency, x.ScheduledAt });
    }
}
