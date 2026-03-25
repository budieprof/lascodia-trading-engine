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

        builder.Property(x => x.BrokerServer)
            .HasMaxLength(200);

        builder.Property(x => x.BrokerName)
            .HasMaxLength(100);

        builder.Property(x => x.AccountType)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(x => x.MarginMode)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(x => x.EncryptedPassword)
            .HasMaxLength(500);

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

        builder.Property(x => x.Leverage)
            .HasPrecision(18, 4);

        builder.Property(x => x.MaxAbsoluteDailyLoss)
            .HasPrecision(18, 2);

        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.IsActive);
        builder.HasIndex(x => new { x.AccountId, x.BrokerServer }).IsUnique()
            .HasFilter("\"IsDeleted\" = false");
    }
}
