using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="MLPeltChangePointLog"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class MLPeltChangePointLogConfiguration : IEntityTypeConfiguration<MLPeltChangePointLog>
{
    public void Configure(EntityTypeBuilder<MLPeltChangePointLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.ToTable("MLPeltChangePointLogs");

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Timeframe).IsRequired().HasMaxLength(20);
        builder.Property(x => x.ChangePointCount).IsRequired();
        builder.Property(x => x.ChangePointIndicesJson).IsRequired().HasColumnType("text");
        builder.Property(x => x.Penalty).IsRequired();
        builder.Property(x => x.TotalCost).IsRequired();
        builder.Property(x => x.ComputedAt).IsRequired();

        builder.HasIndex(x => x.MLModelId);

        builder.HasQueryFilter(e => !e.IsDeleted);
    }
}
