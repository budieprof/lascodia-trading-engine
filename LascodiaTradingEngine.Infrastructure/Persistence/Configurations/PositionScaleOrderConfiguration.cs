using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="PositionScaleOrder"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class PositionScaleOrderConfiguration : IEntityTypeConfiguration<PositionScaleOrder>
{
    public void Configure(EntityTypeBuilder<PositionScaleOrder> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.ScaleType).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.TriggerPips).HasPrecision(18, 8);
        builder.Property(x => x.LotSize).HasPrecision(18, 8);
        builder.Property(x => x.TakeProfitPrice).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.PositionId);
        builder.HasIndex(x => x.OrderId);

        builder.HasOne(x => x.Position)
               .WithMany(x => x.ScaleOrders)
               .HasForeignKey(x => x.PositionId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Order)
               .WithMany(x => x.PositionScaleOrders)
               .HasForeignKey(x => x.OrderId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
