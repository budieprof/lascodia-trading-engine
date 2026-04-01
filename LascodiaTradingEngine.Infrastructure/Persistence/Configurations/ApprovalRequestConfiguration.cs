using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.OperationType).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.Status).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(x => x.TargetEntityType).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Description).IsRequired().HasMaxLength(1000);
        builder.Property(x => x.ChangePayloadJson).IsRequired().HasMaxLength(8000);
        builder.Property(x => x.ApproverComment).HasMaxLength(1000);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.OperationType, x.TargetEntityId, x.Status });

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
