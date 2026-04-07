using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class StrategyGenerationScheduleStateConfiguration : IEntityTypeConfiguration<StrategyGenerationScheduleState>
{
    public void Configure(EntityTypeBuilder<StrategyGenerationScheduleState> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.WorkerName)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(x => x.WorkerName)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");
    }
}
