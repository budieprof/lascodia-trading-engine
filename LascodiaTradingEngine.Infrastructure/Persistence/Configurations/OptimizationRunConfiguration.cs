using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="OptimizationRun"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class OptimizationRunConfiguration : IEntityTypeConfiguration<OptimizationRun>
{
    public void Configure(EntityTypeBuilder<OptimizationRun> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.TriggerType).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(20);

        builder.Property(x => x.BestHealthScore).HasPrecision(5, 4);
        builder.Property(x => x.BaselineHealthScore).HasPrecision(5, 4);

        builder.Property(x => x.ConfigSnapshotJson).HasColumnType("nvarchar(max)");
        builder.Property(x => x.RunMetadataJson).HasColumnType("nvarchar(max)");
        builder.Property(x => x.IntermediateResultsJson).HasColumnType("nvarchar(max)");
        builder.Property(x => x.ApprovalReportJson).HasColumnType("nvarchar(max)");
        builder.Property(x => x.ValidationFollowUpStatus).HasConversion<string>().HasMaxLength(20);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.StrategyId);
        builder.HasIndex(x => new { x.StrategyId, x.Status });
        builder.HasIndex(x => new { x.Status, x.ExecutionLeaseExpiresAt });
        builder.HasIndex(x => new { x.Status, x.DeferredUntilUtc });
        builder.HasIndex(x => x.ValidationFollowUpsCreatedAt);

        builder.HasOne(x => x.Strategy)
               .WithMany(x => x.OptimizationRuns)
               .HasForeignKey(x => x.StrategyId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
