using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class DrawdownSnapshotConfiguration : IEntityTypeConfiguration<DrawdownSnapshot>
{
    public void Configure(EntityTypeBuilder<DrawdownSnapshot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.CurrentEquity).HasPrecision(18, 4);
        builder.Property(x => x.PeakEquity).HasPrecision(18, 4);
        builder.Property(x => x.DrawdownPct).HasPrecision(18, 4);
        builder.Property(x => x.RecoveryMode).HasConversion<string>().IsRequired().HasMaxLength(20);

        builder.HasIndex(x => x.RecordedAt);
    }
}
