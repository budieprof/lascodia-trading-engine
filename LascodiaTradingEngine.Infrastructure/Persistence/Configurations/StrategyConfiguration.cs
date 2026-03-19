using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class StrategyConfiguration : IEntityTypeConfiguration<Strategy>
{
    public void Configure(EntityTypeBuilder<Strategy> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.Symbol)
            .HasMaxLength(10);

        builder.Property(x => x.Timeframe)
            .HasConversion<string>()
            .HasMaxLength(5);

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(x => x.StrategyType)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.HasIndex(x => x.Symbol);
        builder.HasIndex(x => x.RiskProfileId);

        builder.HasOne(x => x.RiskProfile)
               .WithMany(x => x.Strategies)
               .HasForeignKey(x => x.RiskProfileId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}
