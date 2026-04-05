using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="TradeRationale"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class TradeRationaleConfiguration : IEntityTypeConfiguration<TradeRationale>
{
    public void Configure(EntityTypeBuilder<TradeRationale> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.SignalConditionsMet).IsRequired().HasMaxLength(2000);
        builder.Property(x => x.IndicatorValuesJson).IsRequired().HasMaxLength(4000);
        builder.Property(x => x.MLModelVersion).HasMaxLength(100);
        builder.Property(x => x.MLShapContributionsJson).HasMaxLength(4000);
        builder.Property(x => x.RiskCheckDetailsJson).IsRequired().HasMaxLength(8000);
        builder.Property(x => x.Tier1BlockReason).HasMaxLength(500);
        builder.Property(x => x.Tier2BlockReason).HasMaxLength(500);

        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.StrategyType).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.RuleBasedDirection).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.MLPredictedDirection).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.MarketRegimeAtSignal).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.TradingSessionAtSignal).HasConversion<string>().HasMaxLength(30);

        builder.HasOne(x => x.Order)
            .WithMany()
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.TradeSignal)
            .WithMany()
            .HasForeignKey(x => x.TradeSignalId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Strategy)
            .WithMany()
            .HasForeignKey(x => x.StrategyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.RuleBasedConfidence).HasPrecision(18, 8);
        builder.Property(x => x.MLRawProbability).HasPrecision(18, 8);
        builder.Property(x => x.MLCalibratedProbability).HasPrecision(18, 8);
        builder.Property(x => x.MLServedProbability).HasPrecision(18, 8);
        builder.Property(x => x.MLDecisionThreshold).HasPrecision(18, 8);
        builder.Property(x => x.MLConfidenceScore).HasPrecision(18, 8);
        builder.Property(x => x.MLEnsembleDisagreement).HasPrecision(18, 8);
        builder.Property(x => x.MLKellyFraction).HasPrecision(18, 8);
        builder.Property(x => x.AccountEquityAtCheck).HasPrecision(18, 2);
        builder.Property(x => x.ProjectedExposurePct).HasPrecision(18, 4);
        builder.Property(x => x.RiskPerTradePct).HasPrecision(18, 4);
        builder.Property(x => x.RequestedPrice).HasPrecision(18, 8);
        builder.Property(x => x.FillPrice).HasPrecision(18, 8);
        builder.Property(x => x.SlippagePips).HasPrecision(18, 5);
        builder.Property(x => x.SpreadAtExecution).HasPrecision(18, 8);
        builder.Property(x => x.RegimeConfidence).HasPrecision(18, 4);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.OrderId).IsUnique();

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
