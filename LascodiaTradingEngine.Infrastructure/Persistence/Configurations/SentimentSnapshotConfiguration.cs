using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class SentimentSnapshotConfiguration : IEntityTypeConfiguration<SentimentSnapshot>
{
    public void Configure(EntityTypeBuilder<SentimentSnapshot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Currency).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Source).HasConversion<string>().IsRequired().HasMaxLength(50);
        builder.Property(x => x.SentimentScore).HasPrecision(18, 6);
        builder.Property(x => x.Confidence).HasPrecision(18, 6);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Currency, x.CapturedAt });
    }
}
