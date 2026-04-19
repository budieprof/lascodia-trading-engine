using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="PositionLifecycleEvent"/>. The
/// soft-delete query filter must mirror <see cref="Position"/>'s filter because
/// the relationship is required (<c>Position</c> on the principal side) — without
/// matching filters EF logs warning <c>10622</c> that parent-filtered queries
/// may return lifecycle events whose parent is not visible.
/// </summary>
public class PositionLifecycleEventConfiguration : IEntityTypeConfiguration<PositionLifecycleEvent>
{
    public void Configure(EntityTypeBuilder<PositionLifecycleEvent> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.EventType).IsRequired().HasMaxLength(30);
        builder.Property(x => x.Source).IsRequired().HasMaxLength(30);
        builder.Property(x => x.Description).HasMaxLength(500);

        builder.Property(x => x.PreviousLots).HasPrecision(18, 4);
        builder.Property(x => x.NewLots).HasPrecision(18, 4);
        builder.Property(x => x.SwapAccumulated).HasPrecision(18, 8);
        builder.Property(x => x.CommissionAccumulated).HasPrecision(18, 8);

        builder.HasOne(x => x.Position)
            .WithMany()
            .HasForeignKey(x => x.PositionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.PositionId);
        builder.HasIndex(x => x.OccurredAt);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.ToTable("PositionLifecycleEvent");
    }
}
