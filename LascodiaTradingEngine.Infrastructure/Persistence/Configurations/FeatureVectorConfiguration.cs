using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="FeatureVector"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class FeatureVectorConfiguration : IEntityTypeConfiguration<FeatureVector>
{
    public void Configure(EntityTypeBuilder<FeatureVector> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(5);
        builder.Property(x => x.FeatureNamesJson).IsRequired().HasMaxLength(4000);

        // New columns for versioned feature store (nullable for backward compatibility)
        builder.Property(x => x.SchemaHash).HasMaxLength(64);
        builder.Property(x => x.FeatureCount).HasDefaultValue(0);

        builder.HasQueryFilter(x => !x.IsDeleted);

        // Original unique constraint on symbol/timeframe/bar — kept for backward compat.
        // With versioned store, multiple schema versions can exist for the same bar,
        // but the unique index prevents duplicates at the same schema. The store
        // soft-deletes old versions before inserting new ones.
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.BarTimestamp }).IsUnique();
        builder.HasIndex(x => x.CandleId);

        // Composite index for point-in-time queries: WHERE Symbol, Timeframe, BarTimestamp, ComputedAt
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.BarTimestamp, x.ComputedAt })
            .HasDatabaseName("IX_FeatureVector_PointInTime");

        // Index for stale eviction queries: WHERE SchemaHash, ComputedAt
        builder.HasIndex(x => new { x.SchemaHash, x.ComputedAt })
            .HasDatabaseName("IX_FeatureVector_SchemaEviction");

        // Composite index for the feature-generation NOT EXISTS scan that looks up
        // "does this candle already have a FeatureVector at the current schema version?".
        // Observed cost without this index: 6.7s sequential scan on Candle, one row per
        // Candle probing FeatureVector via the single-column CandleId index then filtering
        // in the heap by SchemaVersion. Partial filter on IsDeleted keeps the index small.
        builder.HasIndex(x => new { x.CandleId, x.SchemaVersion })
            .HasDatabaseName("IX_FeatureVector_CandleSchemaLookup")
            .HasFilter("\"IsDeleted\" = false");

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}

/// <summary>
/// EF Core entity configuration for <see cref="FeatureVectorLineage"/>. Defines table mapping,
/// column types, indexes, and the soft-delete query filter.
/// NOTE: Requires migration to create this table.
/// </summary>
public class FeatureVectorLineageConfiguration : IEntityTypeConfiguration<FeatureVectorLineage>
{
    public void Configure(EntityTypeBuilder<FeatureVectorLineage> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(5);
        builder.Property(x => x.SchemaHash).IsRequired().HasMaxLength(64);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.SchemaHash })
            .HasDatabaseName("IX_FeatureVectorLineage_Lookup");

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
