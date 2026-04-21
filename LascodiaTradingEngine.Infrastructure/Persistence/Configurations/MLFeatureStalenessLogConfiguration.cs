using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLFeatureStalenessLog"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLFeatureStalenessLogConfiguration : IEntityTypeConfiguration<MLFeatureStalenessLog>
{
    public void Configure(EntityTypeBuilder<MLFeatureStalenessLog> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        builder.Property(e => e.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(e => e.FeatureName).IsRequired().HasMaxLength(100);
        builder.HasOne(e => e.MLModel).WithMany()
            .HasForeignKey(e => e.MLModelId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.MLModelId, e.FeatureName })
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");
        builder.HasQueryFilter(e => !e.IsDeleted);
    }
}
