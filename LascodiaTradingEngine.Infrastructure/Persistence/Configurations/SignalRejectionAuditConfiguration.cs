using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="SignalRejectionAudit"/>. Immutable
/// append-only table — no soft-delete query filter.
/// </summary>
public class SignalRejectionAuditConfiguration : IEntityTypeConfiguration<SignalRejectionAudit>
{
    public void Configure(EntityTypeBuilder<SignalRejectionAudit> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Stage).IsRequired().HasMaxLength(32);
        builder.Property(x => x.Reason).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Source).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Detail).HasMaxLength(2000);

        // No HasQueryFilter — SignalRejectionAudit has no IsDeleted (immutable).
        builder.HasIndex(x => x.RejectedAt);
        builder.HasIndex(x => new { x.Symbol, x.RejectedAt });
        builder.HasIndex(x => new { x.Stage, x.Reason, x.RejectedAt });
        builder.HasIndex(x => x.TradeSignalId);
        builder.HasIndex(x => new { x.StrategyId, x.RejectedAt });
    }
}
