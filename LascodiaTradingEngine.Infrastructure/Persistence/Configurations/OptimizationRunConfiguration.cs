using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class OptimizationRunConfiguration : IEntityTypeConfiguration<OptimizationRun>
{
    public void Configure(EntityTypeBuilder<OptimizationRun> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.TriggerType).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(20);

        builder.Property(x => x.BestHealthScore).HasPrecision(5, 4);
        builder.Property(x => x.BaselineHealthScore).HasPrecision(5, 4);

        builder.HasIndex(x => x.StrategyId);
        builder.HasIndex(x => new { x.StrategyId, x.Status });

        builder.HasOne(x => x.Strategy)
               .WithMany(x => x.OptimizationRuns)
               .HasForeignKey(x => x.StrategyId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
