using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="StrategyAllocation"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class StrategyAllocationConfiguration : IEntityTypeConfiguration<StrategyAllocation>
{
    public void Configure(EntityTypeBuilder<StrategyAllocation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Weight).HasPrecision(18, 8);
        builder.Property(x => x.RollingSharpRatio).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.StrategyId).IsUnique();

        builder.HasOne(x => x.Strategy)
               .WithMany(x => x.Allocations)
               .HasForeignKey(x => x.StrategyId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
