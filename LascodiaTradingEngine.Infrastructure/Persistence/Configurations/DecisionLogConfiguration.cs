using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="DecisionLog"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class DecisionLogConfiguration : IEntityTypeConfiguration<DecisionLog>
{
    public void Configure(EntityTypeBuilder<DecisionLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.EntityType).IsRequired().HasMaxLength(50);
        builder.Property(x => x.DecisionType).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Outcome).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Source).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Reason).IsRequired();

        // No soft-delete filter — DecisionLog is immutable
        builder.HasIndex(x => new { x.EntityType, x.EntityId });
        builder.HasIndex(x => x.CreatedAt);
    }
}
