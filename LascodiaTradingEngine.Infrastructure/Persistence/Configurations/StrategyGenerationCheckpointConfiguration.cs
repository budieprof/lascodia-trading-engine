using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class StrategyGenerationCheckpointConfiguration : IEntityTypeConfiguration<StrategyGenerationCheckpoint>
{
    public void Configure(EntityTypeBuilder<StrategyGenerationCheckpoint> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.WorkerName).IsRequired().HasMaxLength(100);
        builder.Property(x => x.CycleId).HasMaxLength(64);
        builder.Property(x => x.Fingerprint).IsRequired().HasMaxLength(128);
        builder.Property(x => x.PayloadJson).IsRequired().HasColumnType("text");

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.WorkerName)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");
    }
}
