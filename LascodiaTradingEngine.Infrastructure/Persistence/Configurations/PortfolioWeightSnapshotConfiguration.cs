using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class PortfolioWeightSnapshotConfiguration : IEntityTypeConfiguration<PortfolioWeightSnapshot>
{
    public void Configure(EntityTypeBuilder<PortfolioWeightSnapshot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.AllocationMethod).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Weight).HasPrecision(18, 8);
        builder.Property(x => x.KellyFraction).HasPrecision(18, 8);
        builder.Property(x => x.ObservedSharpe).HasPrecision(18, 6);

        builder.Property(x => x.RowVersion).IsConcurrencyToken();
        builder.HasQueryFilter(x => !x.IsDeleted);

        // Position sizing reads the latest weight per strategy.
        builder.HasIndex(x => new { x.StrategyId, x.ComputedAt });

        builder.HasOne(x => x.Strategy)
               .WithMany()
               .HasForeignKey(x => x.StrategyId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
