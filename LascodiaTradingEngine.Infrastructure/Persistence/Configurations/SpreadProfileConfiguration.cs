using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="SpreadProfile"/>. Defines table mapping,
/// column types, indexes, concurrency token, and the soft-delete query filter.
/// </summary>
public class SpreadProfileConfiguration : IEntityTypeConfiguration<SpreadProfile>
{
    public void Configure(EntityTypeBuilder<SpreadProfile> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.ToTable("SpreadProfiles");

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(x => x.SessionName).HasMaxLength(30);

        builder.Property(x => x.SpreadP25).HasPrecision(18, 8);
        builder.Property(x => x.SpreadP50).HasPrecision(18, 8);
        builder.Property(x => x.SpreadP75).HasPrecision(18, 8);
        builder.Property(x => x.SpreadP95).HasPrecision(18, 8);
        builder.Property(x => x.SpreadMean).HasPrecision(18, 8);

        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasQueryFilter(x => !x.IsDeleted);

        // Composite index for lookups by symbol + hour + day-of-week
        builder.HasIndex(x => new { x.Symbol, x.HourUtc, x.DayOfWeek })
               .HasDatabaseName("IX_SpreadProfile_Symbol_Hour_DOW");

        // Single index on Symbol for bulk loading / purge operations
        builder.HasIndex(x => x.Symbol);
    }
}
