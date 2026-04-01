using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class StressTestResultConfiguration : IEntityTypeConfiguration<StressTestResult>
{
    public void Configure(EntityTypeBuilder<StressTestResult> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.PositionImpactsJson).IsRequired().HasMaxLength(8000);

        builder.HasOne(x => x.Scenario)
            .WithMany()
            .HasForeignKey(x => x.StressTestScenarioId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.TradingAccount)
            .WithMany()
            .HasForeignKey(x => x.TradingAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.PortfolioEquity).HasPrecision(18, 2);
        builder.Property(x => x.StressedPnl).HasPrecision(18, 2);
        builder.Property(x => x.StressedPnlPct).HasPrecision(18, 4);
        builder.Property(x => x.MinimumShockPct).HasPrecision(18, 4);
        builder.Property(x => x.PortfolioVaR95).HasPrecision(18, 2);
        builder.Property(x => x.PortfolioCVaR95).HasPrecision(18, 2);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.TradingAccountId, x.ExecutedAt });

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
