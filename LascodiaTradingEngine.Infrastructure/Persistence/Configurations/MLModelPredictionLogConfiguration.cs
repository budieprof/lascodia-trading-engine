using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

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
        builder.Property(x => x.ActualMagnitudePips).HasPrecision(18, 5);
        builder.Property(x => x.ResolutionSource).HasMaxLength(30);
        builder.Property(x => x.EnsembleDisagreement).HasPrecision(5, 4);
        builder.Property(x => x.ContributionsJson).HasMaxLength(500);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.TradeSignalId);
        builder.HasIndex(x => new { x.MLModelId, x.ModelRole });
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
    }
}
