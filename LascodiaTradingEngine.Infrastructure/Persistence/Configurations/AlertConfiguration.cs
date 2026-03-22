using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.AlertType).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.Channel).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Destination).IsRequired().HasMaxLength(500);
        builder.Property(x => x.ConditionJson).IsRequired().HasMaxLength(1000);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Symbol, x.AlertType, x.IsActive });
    }
}
