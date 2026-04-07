using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="EACommand"/>. Defines table mapping,
/// column types, indexes, and the soft-delete query filter.
/// </summary>
public class EACommandConfiguration : IEntityTypeConfiguration<EACommand>
{
    public void Configure(EntityTypeBuilder<EACommand> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.TargetInstanceId).IsRequired();
        builder.Property(x => x.Symbol).IsRequired();
        builder.Property(x => x.CommandType).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();

        // Soft-delete query filter — automatically excludes deleted commands from queries
        builder.HasQueryFilter(x => !x.IsDeleted);

        // Index for polling pending commands by instance (the primary query pattern)
        builder.HasIndex(x => new { x.TargetInstanceId, x.Acknowledged })
            .HasFilter("\"IsDeleted\" = false");

        // Index for creation-time ordering (used by FIFO command delivery)
        builder.HasIndex(x => x.CreatedAt);

        builder.ToTable("EACommand");
    }
}
