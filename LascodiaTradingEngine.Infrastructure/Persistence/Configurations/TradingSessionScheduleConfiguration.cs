using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="TradingSessionSchedule"/>. Defines table mapping,
/// column types, indexes, and the soft-delete query filter.
/// </summary>
public class TradingSessionScheduleConfiguration : IEntityTypeConfiguration<TradingSessionSchedule>
{
    public void Configure(EntityTypeBuilder<TradingSessionSchedule> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
        builder.Property(x => x.SessionName).IsRequired().HasMaxLength(50);
        builder.Property(x => x.InstanceId).HasMaxLength(100);

        builder.HasQueryFilter(x => !x.IsDeleted);

        // Composite index for upsert lookups by symbol + session + instance
        builder.HasIndex(x => new { x.Symbol, x.SessionName, x.InstanceId })
               .HasDatabaseName("IX_TradingSessionSchedule_Symbol_Session_Instance");

        builder.ToTable("TradingSessionSchedules");
    }
}
