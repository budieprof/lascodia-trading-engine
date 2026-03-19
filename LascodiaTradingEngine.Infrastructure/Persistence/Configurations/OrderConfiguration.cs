using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Session).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.OrderType).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.ExecutionType).HasConversion<string>().IsRequired().HasMaxLength(15);
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.TrailingStopType).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Quantity).HasPrecision(18, 5);
        builder.Property(x => x.Price).HasPrecision(18, 8);
        builder.Property(x => x.StopLoss).HasPrecision(18, 8);
        builder.Property(x => x.TakeProfit).HasPrecision(18, 8);
        builder.Property(x => x.FilledPrice).HasPrecision(18, 8);
        builder.Property(x => x.FilledQuantity).HasPrecision(18, 5);
        builder.Property(x => x.TrailingStopValue).HasPrecision(18, 8);
        builder.Property(x => x.HighestFavourablePrice).HasPrecision(18, 8);

        builder.HasIndex(x => new { x.Symbol, x.Status });
        builder.HasIndex(x => x.TradeSignalId);
        builder.HasIndex(x => x.StrategyId);
        builder.HasIndex(x => x.TradingAccountId);

        builder.HasOne(x => x.TradeSignal)
               .WithMany(x => x.Orders)
               .HasForeignKey(x => x.TradeSignalId)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.Strategy)
               .WithMany(x => x.Orders)
               .HasForeignKey(x => x.StrategyId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.TradingAccount)
               .WithMany(x => x.Orders)
               .HasForeignKey(x => x.TradingAccountId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
