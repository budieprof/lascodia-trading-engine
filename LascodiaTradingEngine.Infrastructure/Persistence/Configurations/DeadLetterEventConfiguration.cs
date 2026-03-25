using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class DeadLetterEventConfiguration : IEntityTypeConfiguration<DeadLetterEvent>
{
    public void Configure(EntityTypeBuilder<DeadLetterEvent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.HasQueryFilter(e => !e.IsDeleted);

        builder.Property(e => e.HandlerName).IsRequired().HasMaxLength(200);
        builder.Property(e => e.EventType).IsRequired().HasMaxLength(200);
        builder.Property(e => e.EventPayload).IsRequired();
        builder.Property(e => e.ErrorMessage).IsRequired().HasMaxLength(2000);
        builder.Property(e => e.StackTrace).HasMaxLength(8000);

        builder.HasIndex(e => e.IsResolved).HasFilter("\"IsResolved\" = false");
        builder.HasIndex(e => e.DeadLetteredAt);
    }
}
