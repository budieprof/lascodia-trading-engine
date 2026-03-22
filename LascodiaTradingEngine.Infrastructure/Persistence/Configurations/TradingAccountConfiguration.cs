using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class TradingAccountConfiguration : IEntityTypeConfiguration<TradingAccount>
{
    public void Configure(EntityTypeBuilder<TradingAccount> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.AccountId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.AccountName)
            .HasMaxLength(200);

        builder.Property(x => x.Currency)
            .HasMaxLength(3);

        builder.Property(x => x.Balance)
            .HasPrecision(18, 8);

        builder.Property(x => x.Equity)
            .HasPrecision(18, 8);

        builder.Property(x => x.MarginUsed)
            .HasPrecision(18, 8);

        builder.Property(x => x.MarginAvailable)
            .HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.BrokerId);
        builder.HasIndex(x => x.IsActive);

        builder.HasOne(x => x.Broker)
               .WithMany(x => x.TradingAccounts)
               .HasForeignKey(x => x.BrokerId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
