using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class MLShadowEvaluationConfiguration : IEntityTypeConfiguration<MLShadowEvaluation>
{
    public void Configure(EntityTypeBuilder<MLShadowEvaluation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(5);
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.PromotionDecision).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.DecisionReason).HasMaxLength(500);

        builder.Property(x => x.ChampionDirectionAccuracy).HasPrecision(5, 4);
        builder.Property(x => x.ChampionMagnitudeCorrelation).HasPrecision(5, 4);
        builder.Property(x => x.ChampionBrierScore).HasPrecision(5, 4);
        builder.Property(x => x.ChallengerDirectionAccuracy).HasPrecision(5, 4);
        builder.Property(x => x.ChallengerMagnitudeCorrelation).HasPrecision(5, 4);
        builder.Property(x => x.ChallengerBrierScore).HasPrecision(5, 4);
        builder.Property(x => x.PromotionThreshold).HasPrecision(5, 4);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Status });

        builder.HasOne(x => x.ChampionModel)
               .WithMany(x => x.ChampionEvaluations)
               .HasForeignKey(x => x.ChampionModelId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.ChallengerModel)
               .WithMany(x => x.ChallengerEvaluations)
               .HasForeignKey(x => x.ChallengerModelId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
