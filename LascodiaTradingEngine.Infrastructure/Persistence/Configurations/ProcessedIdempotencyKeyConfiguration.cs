using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="ProcessedIdempotencyKey"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class ProcessedIdempotencyKeyConfiguration : IEntityTypeConfiguration<ProcessedIdempotencyKey>
{
    public void Configure(EntityTypeBuilder<ProcessedIdempotencyKey> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Key).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Endpoint).IsRequired().HasMaxLength(200);
        builder.Property(x => x.ResponseBodyJson).IsRequired().HasMaxLength(8000);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.Key).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
