using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLTrainingRun"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLTrainingRunConfiguration : IEntityTypeConfiguration<MLTrainingRun>
{
    public void Configure(EntityTypeBuilder<MLTrainingRun> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.TriggerType).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(20);

        builder.Property(x => x.DirectionAccuracy).HasPrecision(5, 4);
        builder.Property(x => x.MagnitudeRMSE).HasPrecision(18, 8);
        builder.Property(x => x.F1Score).HasPrecision(5, 4);
        builder.Property(x => x.ExpectedValue).HasPrecision(18, 8);
        builder.Property(x => x.BrierScore).HasPrecision(5, 4);
        builder.Property(x => x.SharpeRatio).HasPrecision(10, 4);
        // Unlimited text — the hyperparam config JSON routinely exceeds 2500 chars when
        // all self-tuning/adversarial/regularization knobs are populated, which triggered
        // 22001 "value too long for character varying(2048)" on every MLTrainingRun insert
        // during AutoDegrading retrains. Mirrors the EngineConfig.Value fix.
        builder.Property(x => x.HyperparamConfigJson).HasColumnType("text");

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Status });
        builder.HasIndex(x => x.MLModelId);

        // Partial unique index: at most one Queued or Running training run per
        // (Symbol, Timeframe). Closes the TOCTOU race in MLAdwinDriftWorker (and
        // any other auto-retrain trigger) where two concurrent worker instances
        // could insert duplicate Queued runs between the existence-check and the
        // insert. Filter syntax matches PostgreSQL's quoted-identifier convention.
        builder.HasIndex(x => new { x.Symbol, x.Timeframe })
               .IsUnique()
               .HasDatabaseName("UX_MLTrainingRun_Active_Per_Pair")
               .HasFilter("\"Status\" IN ('Queued','Running') AND \"IsDeleted\" = false");

        // ── Improvement 3.4: Data lineage ──
        builder.Property(x => x.DatasetHash).HasMaxLength(64);

        builder.HasOne(x => x.MLModel)
               .WithMany(x => x.TrainingRuns)
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}
