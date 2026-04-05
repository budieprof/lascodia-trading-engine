using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLCpcEncoder"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLCpcEncoderConfiguration : IEntityTypeConfiguration<MLCpcEncoder>
{
    public void Configure(EntityTypeBuilder<MLCpcEncoder> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.InfoNceLoss).HasPrecision(12, 6);
        builder.HasQueryFilter(x => !x.IsDeleted);
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.IsActive });
    }
}
