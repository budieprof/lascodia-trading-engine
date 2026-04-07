using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class StrategyGenerationFeedbackStateConfiguration : IEntityTypeConfiguration<StrategyGenerationFeedbackState>
{
    public void Configure(EntityTypeBuilder<StrategyGenerationFeedbackState> builder)
    {
        builder.ToTable("StrategyGenerationFeedbackState");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.StateKey)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.PayloadJson)
            .IsRequired();

        builder.Property(x => x.LastUpdatedAtUtc)
            .IsRequired();

        builder.Property(x => x.IsDeleted)
            .HasDefaultValue(false);

        builder.HasIndex(x => new { x.StateKey, x.IsDeleted })
            .IsUnique();
    }
}
