using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLKellyFractionLog"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLKellyFractionLogConfiguration : IEntityTypeConfiguration<MLKellyFractionLog>
{
    public void Configure(EntityTypeBuilder<MLKellyFractionLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.ToTable("MLKellyFractionLogs");

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Timeframe).IsRequired().HasMaxLength(20);
        builder.Property(x => x.KellyFraction).IsRequired();
        builder.Property(x => x.HalfKelly).IsRequired();
        builder.Property(x => x.WinRate).IsRequired();
        builder.Property(x => x.WinLossRatio).IsRequired();
        builder.Property(x => x.NegativeEV).IsRequired();
        builder.Property(x => x.ComputedAt).IsRequired();

        builder.HasIndex(x => x.MLModelId);

        builder.HasQueryFilter(e => !e.IsDeleted);
    }
}
