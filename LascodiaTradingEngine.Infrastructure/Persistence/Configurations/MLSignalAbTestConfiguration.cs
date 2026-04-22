using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class MLSignalAbTestConfiguration : IEntityTypeConfiguration<MLSignalAbTest>
{
    public void Configure(EntityTypeBuilder<MLSignalAbTest> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.ToTable("MLSignalAbTest");

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(30);

        builder.HasIndex(x => new { x.Symbol, x.Timeframe })
            .IsUnique()
            .HasFilter("\"Status\" = 'Active' AND \"IsDeleted\" = FALSE");

        builder.HasIndex(x => new { x.ChampionModelId, x.ChallengerModelId, x.Status });
        builder.HasIndex(x => x.StartedAtUtc);

        builder.HasQueryFilter(x => !x.IsDeleted);
        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
