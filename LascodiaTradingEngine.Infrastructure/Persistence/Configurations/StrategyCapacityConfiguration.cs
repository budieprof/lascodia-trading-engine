using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class StrategyCapacityConfiguration : IEntityTypeConfiguration<StrategyCapacity>
{
    public void Configure(EntityTypeBuilder<StrategyCapacity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.MarketImpactCurveJson).IsRequired().HasMaxLength(4000);

        builder.HasOne(x => x.Strategy)
            .WithMany()
            .HasForeignKey(x => x.StrategyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.AverageDailyVolume).HasPrecision(18, 2);
        builder.Property(x => x.VolumeParticipationRatePct).HasPrecision(18, 4);
        builder.Property(x => x.CapacityCeilingLots).HasPrecision(18, 5);
        builder.Property(x => x.CurrentAggregateLots).HasPrecision(18, 5);
        builder.Property(x => x.UtilizationPct).HasPrecision(18, 4);
        builder.Property(x => x.EstimatedSlippageAtCurrentSize).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.StrategyId, x.EstimatedAt });

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
