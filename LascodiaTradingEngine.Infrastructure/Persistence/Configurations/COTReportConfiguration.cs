using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class COTReportConfiguration : IEntityTypeConfiguration<COTReport>
{
    public void Configure(EntityTypeBuilder<COTReport> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Currency).IsRequired().HasMaxLength(10);
        builder.Property(x => x.NetNonCommercialPositioning).HasPrecision(18, 2);
        builder.Property(x => x.NetPositioningChangeWeekly).HasPrecision(18, 2);

        builder.HasIndex(x => new { x.Currency, x.ReportDate });
    }
}
