using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="AlertDispatchLog"/>. Defines column types,
/// max lengths, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class AlertDispatchLogConfiguration : IEntityTypeConfiguration<AlertDispatchLog>
{
    public void Configure(EntityTypeBuilder<AlertDispatchLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Channel).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.Destination).IsRequired().HasMaxLength(500);
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.Message).IsRequired().HasMaxLength(2000);
        builder.Property(x => x.ErrorMessage).HasMaxLength(500);

        builder.HasOne(x => x.Alert)
            .WithMany()
            .HasForeignKey(x => x.AlertId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.AlertId);
        builder.HasIndex(x => x.DispatchedAt);
    }
}
