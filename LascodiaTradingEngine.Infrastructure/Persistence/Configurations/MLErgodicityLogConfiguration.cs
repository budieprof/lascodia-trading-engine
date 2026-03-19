using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class MLErgodicityLogConfiguration : IEntityTypeConfiguration<MLErgodicityLog>
{
    public void Configure(EntityTypeBuilder<MLErgodicityLog> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.ToTable("MLErgodicityLogs");

        builder.Property(e => e.Symbol).IsRequired().HasMaxLength(10);

        builder.Property(e => e.EnsembleGrowthRate).HasColumnType("decimal(18,8)");
        builder.Property(e => e.TimeAverageGrowthRate).HasColumnType("decimal(18,8)");
        builder.Property(e => e.ErgodicityGap).HasColumnType("decimal(18,8)");
        builder.Property(e => e.NaiveKellyFraction).HasColumnType("decimal(18,8)");
        builder.Property(e => e.ErgodicityAdjustedKelly).HasColumnType("decimal(18,8)");
        builder.Property(e => e.GrowthRateVariance).HasColumnType("decimal(18,8)");

        builder.HasQueryFilter(e => !e.IsDeleted);

        builder.HasIndex(e => new { e.MLModelId, e.ComputedAt });
    }
}
