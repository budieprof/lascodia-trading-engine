using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class TradeSignalConfiguration : IEntityTypeConfiguration<TradeSignal>
{
    public void Configure(EntityTypeBuilder<TradeSignal> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol)
            .HasMaxLength(10);

        builder.Property(x => x.Direction)
            .HasConversion<string>()
            .HasMaxLength(10);

        builder.Property(x => x.MLPredictedDirection)
            .HasConversion<string>()
            .HasMaxLength(10);

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(x => x.EntryPrice)
            .HasPrecision(18, 8);

        builder.Property(x => x.StopLoss)
            .HasPrecision(18, 8);

        builder.Property(x => x.TakeProfit)
            .HasPrecision(18, 8);

        builder.Property(x => x.SuggestedLotSize)
            .HasPrecision(18, 8);

        builder.Property(x => x.Confidence)
            .HasPrecision(18, 6);

        builder.Property(x => x.MLConfidenceScore)
            .HasPrecision(18, 6);

        builder.Property(x => x.MLPredictedMagnitude)
            .HasPrecision(18, 6);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.Status });
        builder.HasIndex(x => new { x.StrategyId, x.Status });

        builder.HasOne(x => x.Strategy)
               .WithMany(x => x.TradeSignals)
               .HasForeignKey(x => x.StrategyId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.MLModel)
               .WithMany(x => x.TradeSignals)
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}
