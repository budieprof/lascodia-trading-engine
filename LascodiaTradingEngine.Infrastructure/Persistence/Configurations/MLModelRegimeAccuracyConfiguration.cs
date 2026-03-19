using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class MLModelRegimeAccuracyConfiguration : IEntityTypeConfiguration<MLModelRegimeAccuracy>
{
    public void Configure(EntityTypeBuilder<MLModelRegimeAccuracy> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.Regime).HasConversion<string>().IsRequired().HasMaxLength(20);

        // One row per (model, regime) — upserted, never duplicated.
        builder.HasIndex(x => new { x.MLModelId, x.Regime }).IsUnique();
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Regime });

        builder.HasOne(x => x.MLModel)
               .WithMany()
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
