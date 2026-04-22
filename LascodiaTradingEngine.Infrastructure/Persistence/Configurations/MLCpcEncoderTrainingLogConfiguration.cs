using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>EF Core configuration for <see cref="MLCpcEncoderTrainingLog"/>.</summary>
public class MLCpcEncoderTrainingLogConfiguration : IEntityTypeConfiguration<MLCpcEncoderTrainingLog>
{
    public void Configure(EntityTypeBuilder<MLCpcEncoderTrainingLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Outcome).IsRequired().HasMaxLength(32);
        builder.Property(x => x.Reason).IsRequired().HasMaxLength(64);
        builder.Property(x => x.DiagnosticsJson).HasColumnType("text");
        builder.Property(x => x.TrainInfoNceLoss).HasPrecision(12, 6);
        builder.Property(x => x.ValidationInfoNceLoss).HasPrecision(12, 6);
        builder.Property(x => x.PriorInfoNceLoss).HasPrecision(12, 6);

        // Supports pair/regime drill-downs when investigating stale or rejected encoders.
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Regime, x.EvaluatedAt });

        // Supports operational dashboards that group by promotion/rejection branch.
        builder.HasIndex(x => new { x.Outcome, x.Reason, x.EvaluatedAt });

        // Supports the consecutive-failure counter query (per Symbol/Timeframe/Regime/EncoderType
        // ordered by EvaluatedAt) used by the CpcPretrainerWorker to persist its failure counter.
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Regime, x.EncoderType, x.EvaluatedAt });

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
