using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class BrokerConfiguration : IEntityTypeConfiguration<Broker>
{
    public void Configure(EntityTypeBuilder<Broker> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.BrokerType)
            .HasConversion<string>()
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(x => x.Environment)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(x => x.BaseUrl)
            .HasMaxLength(500);

        builder.Property(x => x.ApiKey)
            .HasMaxLength(500);

        builder.Property(x => x.ApiSecret)
            .HasMaxLength(500);

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.BrokerType);
        builder.HasIndex(x => x.IsActive);

        builder.HasMany(x => x.TradingAccounts)
               .WithOne(x => x.Broker)
               .HasForeignKey(x => x.BrokerId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
