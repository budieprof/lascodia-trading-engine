using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for account-level broker snapshots used by PnL reconciliation.
/// </summary>
public sealed class BrokerAccountSnapshotConfiguration : IEntityTypeConfiguration<BrokerAccountSnapshot>
{
    public void Configure(EntityTypeBuilder<BrokerAccountSnapshot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.InstanceId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.Currency)
            .IsRequired()
            .HasMaxLength(10)
            .HasDefaultValue(string.Empty);

        builder.Property(x => x.Balance).HasPrecision(18, 8);
        builder.Property(x => x.Equity).HasPrecision(18, 8);
        builder.Property(x => x.MarginUsed).HasPrecision(18, 8);
        builder.Property(x => x.FreeMargin).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.TradingAccountId, x.ReportedAt, x.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_BrokerAccountSnapshot_TradingAccount_ReportedAt_Id")
            .HasFilter("\"IsDeleted\" = false");
    }
}
