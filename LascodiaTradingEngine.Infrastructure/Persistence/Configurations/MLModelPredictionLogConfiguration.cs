using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLModelPredictionLog"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLModelPredictionLogConfiguration : IEntityTypeConfiguration<MLModelPredictionLog>
{
    public void Configure(EntityTypeBuilder<MLModelPredictionLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(5);
        builder.Property(x => x.ModelRole).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.PredictedDirection).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.ActualDirection).HasConversion<string>().HasMaxLength(10);

        builder.Property(x => x.PredictedMagnitudePips).HasPrecision(18, 5);
        builder.Property(x => x.ConfidenceScore).HasPrecision(5, 4);
        builder.Property(x => x.RawProbability).HasPrecision(5, 4);
        builder.Property(x => x.CalibratedProbability).HasPrecision(5, 4);
        builder.Property(x => x.ServedCalibratedProbability).HasPrecision(5, 4);
        builder.Property(x => x.DecisionThresholdUsed).HasPrecision(5, 4);
        builder.Property(x => x.ActualMagnitudePips).HasPrecision(18, 5);
        builder.Property(x => x.ResolutionSource).HasMaxLength(30);
        builder.Property(x => x.EnsembleDisagreement).HasPrecision(5, 4);
        builder.Property(x => x.ContributionsJson).HasMaxLength(500);
        builder.Property(x => x.ConformalNonConformityScore).HasPrecision(10, 8);
        builder.Property(x => x.ConformalThresholdUsed).HasPrecision(10, 8);
        builder.Property(x => x.ConformalTargetCoverageUsed).HasPrecision(5, 4);
        builder.Property(x => x.ConformalPredictionSetJson).HasMaxLength(100);
        builder.Property(x => x.RawFeaturesJson).HasMaxLength(4000);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.TradeSignalId);
        builder.HasIndex(x => new { x.MLModelId, x.ModelRole });
        builder.HasIndex(x => new { x.MLModelId, x.ModelRole, x.PredictedAt })
               .HasDatabaseName("IX_MLModelPredictionLog_HorizonAccuracyLookup")
               .HasFilter("\"IsDeleted\" = FALSE");
        builder.HasIndex(x => new { x.MLModelId, x.OutcomeRecordedAt });
        builder.HasIndex(x => new { x.MLModelId, x.WasConformalCovered, x.OutcomeRecordedAt })
               .HasFilter("\"OutcomeRecordedAt\" IS NOT NULL AND \"IsDeleted\" = FALSE");
        builder.HasIndex(x => new { x.MLModelId, x.OutcomeRecordedAt, x.Id })
               .HasDatabaseName("IX_MLModelPredictionLog_ConformalBreakerRecent")
               .HasFilter("\"OutcomeRecordedAt\" IS NOT NULL AND \"ActualDirection\" IS NOT NULL AND \"IsDeleted\" = FALSE");
        builder.HasIndex(x => x.MLConformalCalibrationId);
        // Deduplication guard: one prediction log per (signal, model) pair.
        builder.HasIndex(x => new { x.TradeSignalId, x.MLModelId }).IsUnique();

        builder.HasOne(x => x.TradeSignal)
               .WithMany(x => x.PredictionLogs)
               .HasForeignKey(x => x.TradeSignalId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.MLModel)
               .WithMany(x => x.PredictionLogs)
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.MLConformalCalibration)
               .WithMany()
               .HasForeignKey(x => x.MLConformalCalibrationId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}
