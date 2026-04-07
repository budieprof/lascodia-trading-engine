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
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_OptimizationRun_TerminalRunsRequireResultsPersisted",
                "\"Status\" NOT IN ('Completed','Approved','Rejected') OR \"ResultsPersistedAt\" IS NOT NULL");
            t.HasCheckConstraint(
                "CK_OptimizationRun_ApprovalStatesRequireApprovalEvaluated",
                "\"Status\" NOT IN ('Approved','Rejected') OR \"ApprovalEvaluatedAt\" IS NOT NULL");
            t.HasCheckConstraint(
                "CK_OptimizationRun_CompletionPreparedRequiresPayload",
                "\"CompletionPublicationPreparedAt\" IS NULL OR \"CompletionPublicationPayloadJson\" IS NOT NULL");
            t.HasCheckConstraint(
                "CK_OptimizationRun_CompletionPublishedRequiresPreparedPayload",
                "\"CompletionPublicationStatus\" IS DISTINCT FROM 'Published' " +
                "OR (\"CompletionPublicationPayloadJson\" IS NOT NULL " +
                "AND \"CompletionPublicationPreparedAt\" IS NOT NULL " +
                "AND \"CompletionPublicationCompletedAt\" IS NOT NULL)");
            t.HasCheckConstraint(
                "CK_OptimizationRun_FollowUpStatusRequiresCreation",
                "\"ValidationFollowUpStatus\" IS NULL OR \"ValidationFollowUpsCreatedAt\" IS NOT NULL");
        });

        builder.Property(x => x.TriggerType).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(20);

        builder.Property(x => x.BestHealthScore).HasPrecision(5, 4);
        builder.Property(x => x.BaselineHealthScore).HasPrecision(5, 4);

        builder.Property(x => x.ConfigSnapshotJson).HasColumnType("text");
        builder.Property(x => x.RunMetadataJson).HasColumnType("text");
        builder.Property(x => x.IntermediateResultsJson).HasColumnType("text");
        builder.Property(x => x.ApprovalReportJson).HasColumnType("text");
        builder.Property(x => x.ValidationFollowUpStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.ExecutionLeaseToken).IsConcurrencyToken();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.StrategyId)
            .HasDatabaseName("IX_OptimizationRun_ActivePerStrategy")
            .HasFilter("\"Status\" IN ('Queued','Running') AND \"IsDeleted\" = false")
            .IsUnique();
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
