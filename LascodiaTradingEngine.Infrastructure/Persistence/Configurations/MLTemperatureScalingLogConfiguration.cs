using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class MLTemperatureScalingLogConfiguration : IEntityTypeConfiguration<MLTemperatureScalingLog>
{
    public void Configure(EntityTypeBuilder<MLTemperatureScalingLog> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.ToTable("MLTemperatureScalingLogs");

        builder.Property(e => e.Symbol).IsRequired().HasMaxLength(50);
        builder.Property(e => e.Timeframe).IsRequired().HasMaxLength(50);

        builder.HasIndex(e => e.MLModelId);

        builder.HasQueryFilter(e => !e.IsDeleted);
    }
}
