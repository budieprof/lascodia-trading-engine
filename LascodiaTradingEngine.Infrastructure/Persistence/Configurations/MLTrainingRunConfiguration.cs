using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

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
        builder.Property(x => x.HyperparamConfigJson).HasMaxLength(2048);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Status });
        builder.HasIndex(x => x.MLModelId);

        builder.HasOne(x => x.MLModel)
               .WithMany(x => x.TrainingRuns)
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}
