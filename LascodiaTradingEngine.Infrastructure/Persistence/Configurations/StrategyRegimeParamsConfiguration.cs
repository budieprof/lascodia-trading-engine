using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class StrategyRegimeParamsConfiguration : IEntityTypeConfiguration<StrategyRegimeParams>
{
    public void Configure(EntityTypeBuilder<StrategyRegimeParams> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Regime)
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(x => x.ParametersJson)
            .IsRequired();

        builder.Property(x => x.HealthScore).HasPrecision(18, 4);
        builder.Property(x => x.HealthScoreCILower).HasPrecision(18, 4);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.StrategyId, x.Regime }).IsUnique()
            .HasFilter("\"IsDeleted\" = false");

        builder.HasOne(x => x.Strategy)
            .WithMany()
            .HasForeignKey(x => x.StrategyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.OptimizationRun)
            .WithMany()
            .HasForeignKey(x => x.OptimizationRunId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
