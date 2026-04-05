using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="COTReport"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class COTReportConfiguration : IEntityTypeConfiguration<COTReport>
{
    public void Configure(EntityTypeBuilder<COTReport> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Currency).IsRequired().HasMaxLength(10);
        builder.Property(x => x.NetNonCommercialPositioning).HasPrecision(18, 2);
        builder.Property(x => x.NetPositioningChangeWeekly).HasPrecision(18, 2);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Currency, x.ReportDate }).IsUnique();
    }
}
