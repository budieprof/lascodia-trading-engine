using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="RiskProfile"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class RiskProfileConfiguration : IEntityTypeConfiguration<RiskProfile>
{
    public void Configure(EntityTypeBuilder<RiskProfile> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Name)
            .HasMaxLength(100);

        builder.Property(x => x.MaxLotSizePerTrade)
            .HasPrecision(18, 8);

        builder.Property(x => x.MaxDailyDrawdownPct)
            .HasPrecision(18, 8);

        builder.Property(x => x.MaxTotalDrawdownPct)
            .HasPrecision(18, 8);

        builder.Property(x => x.MaxRiskPerTradePct)
            .HasPrecision(18, 8);

        builder.Property(x => x.MaxSymbolExposurePct)
            .HasPrecision(18, 8);

        builder.Property(x => x.DrawdownRecoveryThresholdPct)
            .HasPrecision(18, 8);

        builder.Property(x => x.RecoveryLotSizeMultiplier)
            .HasPrecision(18, 8);

        builder.Property(x => x.RecoveryExitThresholdPct)
            .HasPrecision(18, 8);

        builder.Property(x => x.MaxTotalExposurePct)
            .HasPrecision(18, 8);

        builder.Property(x => x.MaxAbsoluteRiskPerTrade)
            .HasPrecision(18, 4);

        builder.Property(x => x.MinStopLossDistancePips)
            .HasPrecision(18, 8);

        builder.Property(x => x.MinRiskRewardRatio)
            .HasPrecision(18, 8);

        builder.Property(x => x.WeekendGapRiskMultiplier)
            .HasPrecision(18, 8);

        builder.Property(x => x.MinEquityFloor)
            .HasPrecision(18, 4);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.IsDefault);
    }
}
