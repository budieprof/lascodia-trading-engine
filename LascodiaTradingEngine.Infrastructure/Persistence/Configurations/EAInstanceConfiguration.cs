using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class EAInstanceConfiguration : IEntityTypeConfiguration<EAInstance>
{
    public void Configure(EntityTypeBuilder<EAInstance> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.InstanceId).IsRequired();
        builder.Property(x => x.Symbols).IsRequired();
        builder.Property(x => x.ChartSymbol).IsRequired();
        builder.Property(x => x.ChartTimeframe).IsRequired();
        builder.Property(x => x.EAVersion).IsRequired();

        builder.HasOne(x => x.TradingAccount)
            .WithMany()
            .HasForeignKey(x => x.TradingAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.TradingAccountId);

        // Unique index on InstanceId to prevent duplicate registrations (excludes soft-deleted rows)
        builder.HasIndex(x => x.InstanceId).IsUnique()
            .HasFilter("\"IsDeleted\" = false");

        builder.ToTable("EAInstance");
    }
}
