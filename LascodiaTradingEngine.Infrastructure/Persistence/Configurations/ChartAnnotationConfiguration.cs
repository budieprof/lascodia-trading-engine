using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF configuration for <see cref="ChartAnnotation"/>. The
/// <c>(Target, AnnotatedAt)</c> covering index powers the hot-path "annotations
/// on this chart in this time window" query without scanning the full table.
/// </summary>
public class ChartAnnotationConfiguration : IEntityTypeConfiguration<ChartAnnotation>
{
    public void Configure(EntityTypeBuilder<ChartAnnotation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Target).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Symbol).HasMaxLength(12);
        builder.Property(x => x.Body).IsRequired().HasMaxLength(500);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Target, x.AnnotatedAt });
        builder.HasIndex(x => x.CreatedBy);
    }
}
