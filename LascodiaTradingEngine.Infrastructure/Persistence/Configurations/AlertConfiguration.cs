using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="Alert"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.AlertType).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.Symbol).HasMaxLength(10);
        builder.Property(x => x.ConditionJson).IsRequired().HasMaxLength(1000);

        builder.Property(x => x.Severity).HasConversion<string>().HasMaxLength(10);
        builder.Property(x => x.DeduplicationKey).HasMaxLength(200);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.AlertType, x.IsActive });
    }
}
