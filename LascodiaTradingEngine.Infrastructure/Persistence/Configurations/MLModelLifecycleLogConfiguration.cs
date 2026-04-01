using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class MLModelLifecycleLogConfiguration : IEntityTypeConfiguration<MLModelLifecycleLog>
{
    public void Configure(EntityTypeBuilder<MLModelLifecycleLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.EventType).IsRequired().HasMaxLength(50);
        builder.Property(x => x.PreviousStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.NewStatus).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.Reason).IsRequired().HasMaxLength(1000);

        // FK relationship configured from MLModelConfiguration.HasMany(LifecycleLogs)

        builder.Property(x => x.DirectionAccuracyAtTransition).HasPrecision(18, 8);
        builder.Property(x => x.LiveAccuracyAtTransition).HasPrecision(18, 8);
        builder.Property(x => x.BrierScoreAtTransition).HasPrecision(18, 8);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.MLModelId, x.OccurredAt });

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
