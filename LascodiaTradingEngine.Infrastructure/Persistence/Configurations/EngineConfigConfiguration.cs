using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="EngineConfig"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class EngineConfigConfiguration : IEntityTypeConfiguration<EngineConfig>
{
    public void Configure(EntityTypeBuilder<EngineConfig> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Key)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(x => x.Value)
            .IsRequired()
            .HasMaxLength(2000);

        builder.Property(x => x.Description)
            .HasMaxLength(500);

        builder.Property(x => x.DataType)
            .HasConversion<string>()
            .IsRequired()
            .HasMaxLength(20);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.Key).IsUnique();
    }
}
