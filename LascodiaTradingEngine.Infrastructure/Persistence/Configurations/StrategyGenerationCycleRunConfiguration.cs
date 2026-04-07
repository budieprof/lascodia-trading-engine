using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class StrategyGenerationCycleRunConfiguration : IEntityTypeConfiguration<StrategyGenerationCycleRun>
{
    public void Configure(EntityTypeBuilder<StrategyGenerationCycleRun> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.WorkerName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.CycleId)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(x => x.Fingerprint)
            .HasMaxLength(128);

        builder.Property(x => x.FailureStage)
            .HasMaxLength(64);

        builder.Property(x => x.FailureMessage)
            .HasMaxLength(2000);

        builder.HasIndex(x => x.CycleId)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");

        builder.HasIndex(x => new { x.WorkerName, x.StartedAtUtc, x.IsDeleted });
    }
}
