using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF configuration for <see cref="ReconciliationRun"/>. Immutable append-only
/// table keyed by InstanceId + RunAt so the monitor can pull the last N minutes
/// of runs without scanning the full table.
/// </summary>
public class ReconciliationRunConfiguration : IEntityTypeConfiguration<ReconciliationRun>
{
    public void Configure(EntityTypeBuilder<ReconciliationRun> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.InstanceId).IsRequired().HasMaxLength(64);

        builder.HasIndex(x => x.RunAt);
        builder.HasIndex(x => new { x.InstanceId, x.RunAt });
    }
}
