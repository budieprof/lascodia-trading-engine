using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="WorkerHealthSnapshot"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class WorkerHealthSnapshotConfiguration : IEntityTypeConfiguration<WorkerHealthSnapshot>
{
    public void Configure(EntityTypeBuilder<WorkerHealthSnapshot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.WorkerName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.LastErrorMessage).HasMaxLength(500);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.WorkerName, x.CapturedAt });

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
