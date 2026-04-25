using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>EF Core configuration for <see cref="MLCalibrationLog"/>.</summary>
public class MLCalibrationLogConfiguration : IEntityTypeConfiguration<MLCalibrationLog>
{
    public void Configure(EntityTypeBuilder<MLCalibrationLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Outcome).IsRequired().HasMaxLength(32);
        builder.Property(x => x.Reason).IsRequired().HasMaxLength(64);
        builder.Property(x => x.AlertState).IsRequired().HasMaxLength(16);
        builder.Property(x => x.DiagnosticsJson).HasColumnType("text");

        builder.Property(x => x.CurrentEce).HasPrecision(10, 6);
        builder.Property(x => x.PreviousEce).HasPrecision(10, 6);
        builder.Property(x => x.BaselineEce).HasPrecision(10, 6);
        builder.Property(x => x.TrendDelta).HasPrecision(10, 6);
        builder.Property(x => x.BaselineDelta).HasPrecision(10, 6);
        builder.Property(x => x.Accuracy).HasPrecision(10, 6);
        builder.Property(x => x.MeanConfidence).HasPrecision(10, 6);
        builder.Property(x => x.EceStderr).HasPrecision(10, 6);

        // Per-model dashboards drill chronologically.
        builder.HasIndex(x => new { x.MLModelId, x.EvaluatedAt });
        // Symbol/regime dashboards.
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Regime, x.EvaluatedAt });
        // Incident view: filter by alert state across the fleet.
        builder.HasIndex(x => new { x.AlertState, x.EvaluatedAt });
        // Cross-restart stale-data short-circuit lookup (mirrors threshold worker pattern).
        builder.HasIndex(x => new { x.MLModelId, x.NewestOutcomeAt });

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
