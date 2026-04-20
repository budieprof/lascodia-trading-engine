using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF configuration for <see cref="CalibrationSnapshot"/>. Unique index on
/// <c>(PeriodStart, PeriodGranularity, Stage, Reason)</c> enforces idempotent
/// worker re-runs — a second pass for the same period is a no-op instead of
/// inserting duplicate rows.
/// </summary>
public class CalibrationSnapshotConfiguration : IEntityTypeConfiguration<CalibrationSnapshot>
{
    public void Configure(EntityTypeBuilder<CalibrationSnapshot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.PeriodGranularity).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Stage).IsRequired().HasMaxLength(32);
        builder.Property(x => x.Reason).IsRequired().HasMaxLength(64);

        builder.HasIndex(x => x.PeriodStart);
        builder.HasIndex(x => new { x.PeriodStart, x.PeriodGranularity, x.Stage, x.Reason })
            .IsUnique()
            .HasDatabaseName("IX_CalibrationSnapshot_Period_Stage_Reason_Unique");
    }
}
