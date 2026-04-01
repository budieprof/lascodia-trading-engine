using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class TransactionCostAnalysisConfiguration : IEntityTypeConfiguration<TransactionCostAnalysis>
{
    public void Configure(EntityTypeBuilder<TransactionCostAnalysis> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);

        builder.HasOne(x => x.Order)
            .WithMany()
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.TradeSignal)
            .WithMany()
            .HasForeignKey(x => x.TradeSignalId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(x => x.ArrivalPrice).HasPrecision(18, 8);
        builder.Property(x => x.FillPrice).HasPrecision(18, 8);
        builder.Property(x => x.SubmissionPrice).HasPrecision(18, 8);
        builder.Property(x => x.VwapBenchmark).HasPrecision(18, 8);
        builder.Property(x => x.ImplementationShortfall).HasPrecision(18, 8);
        builder.Property(x => x.DelayCost).HasPrecision(18, 8);
        builder.Property(x => x.MarketImpactCost).HasPrecision(18, 8);
        builder.Property(x => x.SpreadCost).HasPrecision(18, 8);
        builder.Property(x => x.CommissionCost).HasPrecision(18, 8);
        builder.Property(x => x.TotalCost).HasPrecision(18, 8);
        builder.Property(x => x.TotalCostBps).HasPrecision(18, 4);
        builder.Property(x => x.Quantity).HasPrecision(18, 5);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.OrderId).IsUnique();
        builder.HasIndex(x => new { x.Symbol, x.AnalyzedAt });

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
