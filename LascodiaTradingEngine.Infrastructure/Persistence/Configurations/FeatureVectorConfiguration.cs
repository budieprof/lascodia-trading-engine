using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class FeatureVectorConfiguration : IEntityTypeConfiguration<FeatureVector>
{
    public void Configure(EntityTypeBuilder<FeatureVector> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(5);
        builder.Property(x => x.FeatureNamesJson).IsRequired().HasMaxLength(4000);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.BarTimestamp }).IsUnique();
        builder.HasIndex(x => x.CandleId);

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
