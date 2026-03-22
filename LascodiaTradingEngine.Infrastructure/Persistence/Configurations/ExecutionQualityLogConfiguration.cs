using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class ExecutionQualityLogConfiguration : IEntityTypeConfiguration<ExecutionQualityLog>
{
    public void Configure(EntityTypeBuilder<ExecutionQualityLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Session).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.RequestedPrice).HasPrecision(18, 8);
        builder.Property(x => x.FilledPrice).HasPrecision(18, 8);
        builder.Property(x => x.SlippagePips).HasPrecision(18, 8);
        builder.Property(x => x.FillRate).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.Session });
        builder.HasIndex(x => x.OrderId).IsUnique();

        builder.HasIndex(x => x.StrategyId);

        builder.HasOne(x => x.Order)
               .WithOne(x => x.ExecutionQualityLog)
               .HasForeignKey<ExecutionQualityLog>(x => x.OrderId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Strategy)
               .WithMany(x => x.ExecutionQualityLogs)
               .HasForeignKey(x => x.StrategyId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}
