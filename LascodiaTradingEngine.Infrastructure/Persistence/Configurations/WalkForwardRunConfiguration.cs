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

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.InitialBalance).HasPrecision(18, 2);
        builder.Property(x => x.AverageOutOfSampleScore).HasPrecision(18, 6);
        builder.Property(x => x.ScoreConsistency).HasPrecision(18, 6);
        builder.Property(x => x.ParametersSnapshotJson).HasColumnType("text");

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.StrategyId);
        builder.HasIndex(x => new { x.StrategyId, x.Status });
        builder.HasIndex(x => x.SourceOptimizationRunId)
            .IsUnique()
            .HasFilter("\"SourceOptimizationRunId\" IS NOT NULL AND \"IsDeleted\" = false");

        builder.HasOne(x => x.Strategy)
               .WithMany(x => x.WalkForwardRuns)
               .HasForeignKey(x => x.StrategyId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
