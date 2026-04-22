using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for terminal signal-level A/B test decisions.
/// </summary>
public class MLSignalAbTestResultConfiguration : IEntityTypeConfiguration<MLSignalAbTestResult>
{
    public void Configure(EntityTypeBuilder<MLSignalAbTestResult> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.ToTable("MLSignalAbTestResult");

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.Decision).IsRequired().HasMaxLength(40);
        builder.Property(x => x.Reason).IsRequired().HasMaxLength(2000);

        builder.Property(x => x.ChampionAvgPnl).HasPrecision(18, 8);
        builder.Property(x => x.ChallengerAvgPnl).HasPrecision(18, 8);
        builder.Property(x => x.ChampionSharpe).HasPrecision(18, 8);
        builder.Property(x => x.ChallengerSharpe).HasPrecision(18, 8);
        builder.Property(x => x.SprtLogLikelihoodRatio).HasPrecision(18, 8);
        builder.Property(x => x.CovariateImbalanceScore).HasPrecision(18, 8);

        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.CompletedAtUtc });
        builder.HasIndex(x => new { x.ChampionModelId, x.ChallengerModelId, x.StartedAtUtc })
            .IsUnique();

        builder.HasQueryFilter(x => !x.IsDeleted);
        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
