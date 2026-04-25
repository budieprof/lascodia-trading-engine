using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>EF Core configuration for <see cref="MLAdaptiveThresholdLog"/>.</summary>
public class MLAdaptiveThresholdLogConfiguration : IEntityTypeConfiguration<MLAdaptiveThresholdLog>
{
    public void Configure(EntityTypeBuilder<MLAdaptiveThresholdLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Outcome).IsRequired().HasMaxLength(32);
        builder.Property(x => x.Reason).IsRequired().HasMaxLength(64);
        builder.Property(x => x.DiagnosticsJson).HasColumnType("text");

        builder.Property(x => x.PreviousThreshold).HasPrecision(10, 6);
        builder.Property(x => x.OptimalThreshold).HasPrecision(10, 6);
        builder.Property(x => x.NewThreshold).HasPrecision(10, 6);
        builder.Property(x => x.Drift).HasPrecision(10, 6);
        builder.Property(x => x.HoldoutEvAtNewThreshold).HasPrecision(10, 6);
        builder.Property(x => x.HoldoutEvAtPreviousThreshold).HasPrecision(10, 6);
        builder.Property(x => x.HoldoutMeanPnlPips).HasPrecision(14, 6);
        builder.Property(x => x.StationarityPsi).HasPrecision(10, 6);

        builder.HasIndex(x => new { x.MLModelId, x.EvaluatedAt });
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Regime, x.EvaluatedAt });
        builder.HasIndex(x => new { x.Outcome, x.Reason, x.EvaluatedAt });
        // Supports the post-restart stale-data short-circuit lookup: per-model latest
        // NewestOutcomeAt across all audit rows.
        builder.HasIndex(x => new { x.MLModelId, x.NewestOutcomeAt });

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
