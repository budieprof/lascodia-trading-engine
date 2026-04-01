using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class EngineConfigAuditLogConfiguration : IEntityTypeConfiguration<EngineConfigAuditLog>
{
    public void Configure(EntityTypeBuilder<EngineConfigAuditLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Key).IsRequired().HasMaxLength(200);
        builder.Property(x => x.OldValue).HasMaxLength(2000);
        builder.Property(x => x.NewValue).IsRequired().HasMaxLength(2000);
        builder.Property(x => x.Reason).HasMaxLength(1000);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.Key, x.ChangedAt });

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
