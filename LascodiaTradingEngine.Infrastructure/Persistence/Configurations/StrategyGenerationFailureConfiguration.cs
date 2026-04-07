using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class StrategyGenerationFailureConfiguration : IEntityTypeConfiguration<StrategyGenerationFailure>
{
    public void Configure(EntityTypeBuilder<StrategyGenerationFailure> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.CandidateId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.CycleId).HasMaxLength(64);
        builder.Property(x => x.CandidateHash).IsRequired().HasMaxLength(128);
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.StrategyType).HasConversion<string>().IsRequired().HasMaxLength(50);
        builder.Property(x => x.ParametersJson).IsRequired().HasColumnType("text");
        builder.Property(x => x.FailureStage).IsRequired().HasMaxLength(100);
        builder.Property(x => x.FailureReason).IsRequired().HasMaxLength(200);
        builder.Property(x => x.DetailsJson).HasColumnType("text");

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.CandidateId, x.FailureStage, x.ResolvedAtUtc, x.IsDeleted });
        builder.HasIndex(x => new { x.IsReported, x.ResolvedAtUtc, x.IsDeleted });
    }
}
