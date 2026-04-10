using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="WalkForwardRun"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class WalkForwardRunConfiguration : IEntityTypeConfiguration<WalkForwardRun>
{
    public void Configure(EntityTypeBuilder<WalkForwardRun> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_WalkForwardRun_PositiveWindowDays",
                "\"InSampleDays\" > 0 AND \"OutOfSampleDays\" > 0");
        });

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.QueueSource).HasConversion<string>().IsRequired().HasMaxLength(64);
        builder.Property(x => x.FailureCode).HasConversion<string>().HasMaxLength(128);
        builder.Property(x => x.InitialBalance).HasPrecision(18, 2);
        builder.Property(x => x.AverageOutOfSampleScore).HasPrecision(18, 6);
        builder.Property(x => x.ScoreConsistency).HasPrecision(18, 6);
        builder.Property(x => x.ParametersSnapshotJson).HasColumnType("text");
        builder.Property(x => x.StrategySnapshotJson).HasColumnType("text");
        builder.Property(x => x.BacktestOptionsSnapshotJson).HasColumnType("text");
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);
        builder.Property(x => x.FailureDetailsJson).HasColumnType("text");
        builder.Property(x => x.ClaimedByWorkerId).HasMaxLength(256);
        builder.Property(x => x.ExecutionLeaseToken).IsConcurrencyToken();

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.StrategyId);
        builder.HasIndex(x => new { x.StrategyId, x.Status });
        builder.HasIndex(x => new { x.Status, x.ExecutionLeaseExpiresAt });
        builder.HasIndex(x => new { x.Status, x.AvailableAt, x.Priority, x.QueuedAt, x.Id });
        builder.HasIndex(x => x.SourceOptimizationRunId)
            .IsUnique()
            .HasFilter("\"SourceOptimizationRunId\" IS NOT NULL AND \"IsDeleted\" = false");
        builder.HasIndex(x => x.ValidationQueueKey)
            .HasDatabaseName("IX_WalkForwardRun_ActiveValidationQueueKey")
            .IsUnique()
            .HasFilter("\"ValidationQueueKey\" IS NOT NULL AND \"Status\" IN ('Queued','Running') AND \"IsDeleted\" = false");

        builder.HasOne(x => x.Strategy)
               .WithMany(x => x.WalkForwardRuns)
               .HasForeignKey(x => x.StrategyId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
