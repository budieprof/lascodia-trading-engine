using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class StrategyGenerationPendingArtifactConfiguration : IEntityTypeConfiguration<StrategyGenerationPendingArtifact>
{
    public void Configure(EntityTypeBuilder<StrategyGenerationPendingArtifact> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.CandidateId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.CycleId).HasMaxLength(64);
        builder.Property(x => x.CandidatePayloadJson).IsRequired().HasColumnType("text");
        builder.Property(x => x.LastErrorMessage).HasMaxLength(1000);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.CandidateId)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");
        builder.HasIndex(x => x.StrategyId);
    }
}
