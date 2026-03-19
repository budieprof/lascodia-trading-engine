using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class MLShadowRegimeBreakdownConfiguration : IEntityTypeConfiguration<MLShadowRegimeBreakdown>
{
    public void Configure(EntityTypeBuilder<MLShadowRegimeBreakdown> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Regime).HasConversion<string>().IsRequired().HasMaxLength(20);

        builder.Property(x => x.ChampionAccuracy).HasColumnType("decimal(8,6)");
        builder.Property(x => x.ChallengerAccuracy).HasColumnType("decimal(8,6)");
        builder.Property(x => x.AccuracyDelta).HasColumnType("decimal(8,6)");

        // One row per (evaluation, regime)
        builder.HasIndex(x => new { x.ShadowEvaluationId, x.Regime }).IsUnique();

        builder.HasOne(x => x.ShadowEvaluation)
               .WithMany(e => e.RegimeBreakdowns)
               .HasForeignKey(x => x.ShadowEvaluationId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
