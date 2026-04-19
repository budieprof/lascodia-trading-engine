using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLModel"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLModelConfiguration : IEntityTypeConfiguration<MLModel>
{
    public void Configure(EntityTypeBuilder<MLModel> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.ModelVersion).IsRequired().HasMaxLength(50);
        builder.Property(x => x.FilePath).HasMaxLength(500); // optional — model bytes stored in ModelBytes
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(20);

        builder.Property(x => x.ModelBytes); // varbinary(max) / bytea — stored in DB
        builder.Property(x => x.OnnxBytes);  // varbinary(max) / bytea — optional ONNX inference graph

        builder.Property(x => x.DirectionAccuracy).HasPrecision(5, 4);
        builder.Property(x => x.MagnitudeRMSE).HasPrecision(18, 8);
        builder.Property(x => x.F1Score).HasPrecision(5, 4);
        builder.Property(x => x.ExpectedValue).HasPrecision(18, 8);
        builder.Property(x => x.BrierScore).HasPrecision(5, 4);
        builder.Property(x => x.WalkForwardAvgAccuracy).HasPrecision(5, 4);
        builder.Property(x => x.WalkForwardStdDev).HasPrecision(5, 4);
        builder.Property(x => x.SharpeRatio).HasPrecision(10, 4);
        builder.Property(x => x.PlattA).HasPrecision(10, 6);
        builder.Property(x => x.PlattB).HasPrecision(10, 6);
        builder.Property(x => x.RegimeScope).HasMaxLength(20);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.IsActive });
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.RegimeScope, x.IsActive });

        builder.HasMany(x => x.TrainingRuns)
               .WithOne(x => x.MLModel)
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(x => x.TradeSignals)
               .WithOne(x => x.MLModel)
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(x => x.PredictionLogs)
               .WithOne(x => x.MLModel)
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Restrict);

        // ── Rec #1: LearnerArchitecture ───────────────────────────────────────
        builder.Property(x => x.LearnerArchitecture).HasConversion<string>().HasMaxLength(30);

        // ── Rec #4 + #6: Transfer / distillation FKs (self-referencing) ──────
        builder.HasOne<MLModel>()
               .WithMany()
               .HasForeignKey(x => x.TransferredFromModelId)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<MLModel>()
               .WithMany()
               .HasForeignKey(x => x.DistilledFromModelId)
               .OnDelete(DeleteBehavior.SetNull);

        // ── Improvements 3.2, 10.3: Rollback chain & robustness ──
        builder.Property(x => x.FragilityScore).HasPrecision(18, 8);
        builder.Property(x => x.DatasetHash).HasMaxLength(64); // SHA-256 hex

        builder.HasOne<MLModel>()
               .WithMany()
               .HasForeignKey(x => x.PreviousChampionModelId)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(x => x.LifecycleLogs)
               .WithOne(x => x.MLModel)
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
