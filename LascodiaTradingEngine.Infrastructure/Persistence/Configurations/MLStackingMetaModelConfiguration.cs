using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLStackingMetaModel"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLStackingMetaModelConfiguration : IEntityTypeConfiguration<MLStackingMetaModel>
{
    public void Configure(EntityTypeBuilder<MLStackingMetaModel> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.BaseModelIdsJson).IsRequired();
        builder.Property(x => x.MetaWeightsJson).IsRequired();
        builder.Property(x => x.MetaBias).HasPrecision(10, 8);
        builder.Property(x => x.DirectionAccuracy).HasPrecision(5, 4);
        builder.Property(x => x.BrierScore).HasPrecision(5, 4);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.IsActive });
    }
}
