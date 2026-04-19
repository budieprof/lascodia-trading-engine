using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="PaperExecution"/>. Paper fills power the forward-test
/// gate in <c>PromotionGateValidator</c>; indexes optimise the promotion-time count /
/// earliest-fill queries and the monitor worker's "open rows per symbol" scan.
/// </summary>
public class PaperExecutionConfiguration : IEntityTypeConfiguration<PaperExecution>
{
    public void Configure(EntityTypeBuilder<PaperExecution> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Direction).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(x => x.Timeframe).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.SimulatedExitReason).HasConversion<string?>().HasMaxLength(30);

        builder.Property(x => x.RequestedEntryPrice).HasPrecision(18, 8);
        builder.Property(x => x.SimulatedFillPrice).HasPrecision(18, 8);
        builder.Property(x => x.SimulatedSlippagePriceUnits).HasPrecision(18, 8);
        builder.Property(x => x.SimulatedSpreadCostPriceUnits).HasPrecision(18, 8);
        builder.Property(x => x.SimulatedCommissionAccountCcy).HasPrecision(18, 4);
        builder.Property(x => x.LotSize).HasPrecision(18, 4);
        builder.Property(x => x.ContractSize).HasPrecision(18, 4);
        builder.Property(x => x.PipSize).HasPrecision(18, 8);
        builder.Property(x => x.StopLoss).HasPrecision(18, 8);
        builder.Property(x => x.TakeProfit).HasPrecision(18, 8);
        builder.Property(x => x.SimulatedExitPrice).HasPrecision(18, 8);
        builder.Property(x => x.SimulatedGrossPnL).HasPrecision(18, 4);
        builder.Property(x => x.SimulatedNetPnL).HasPrecision(18, 4);
        builder.Property(x => x.SimulatedMaeAbsolute).HasPrecision(18, 8);
        builder.Property(x => x.SimulatedMfeAbsolute).HasPrecision(18, 8);

        builder.Property(x => x.RowVersion).IsConcurrencyToken();
        builder.HasQueryFilter(x => !x.IsDeleted);

        // Hot path: count closed + earliest open paper fills for a given strategy at promotion time.
        builder.HasIndex(x => new { x.StrategyId, x.SignalGeneratedAt });
        builder.HasIndex(x => new { x.StrategyId, x.Status });
        // Monitor worker scans open rows by symbol on every tick.
        builder.HasIndex(x => new { x.Symbol, x.Status });
        // Promotion gate filters synthetic vs. live-sourced rows; index the combined key.
        builder.HasIndex(x => new { x.StrategyId, x.IsSynthetic });

        builder.HasOne(x => x.Strategy)
               .WithMany()
               .HasForeignKey(x => x.StrategyId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.TradeSignal)
               .WithMany()
               .HasForeignKey(x => x.TradeSignalId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}
