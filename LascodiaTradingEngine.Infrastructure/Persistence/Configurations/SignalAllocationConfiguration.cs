using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="SignalAllocation"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class SignalAllocationConfiguration : IEntityTypeConfiguration<SignalAllocation>
{
    public void Configure(EntityTypeBuilder<SignalAllocation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.AllocationMethod).IsRequired().HasMaxLength(30);
        builder.Property(x => x.RiskCheckBlockReason).HasMaxLength(500);

        builder.HasOne(x => x.TradeSignal)
            .WithMany()
            .HasForeignKey(x => x.TradeSignalId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.TradingAccount)
            .WithMany()
            .HasForeignKey(x => x.TradingAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Order)
            .WithMany()
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(x => x.AllocatedLotSize).HasPrecision(18, 5);
        builder.Property(x => x.AccountEquityAtAllocation).HasPrecision(18, 2);
        builder.Property(x => x.AllocationFraction).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.TradeSignalId);

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
