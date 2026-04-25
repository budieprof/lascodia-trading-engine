using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="DistributedLockLease"/>. Enforces a unique
/// <see cref="DistributedLockLease.Key"/> so the underlying upsert pattern in
/// <c>LeaseBasedDistributedLock</c> can use <c>ON CONFLICT (Key)</c>, and indexes on
/// <see cref="DistributedLockLease.ExpiresAtUtc"/> to keep expiry sweeps cheap.
/// </summary>
public class DistributedLockLeaseConfiguration : IEntityTypeConfiguration<DistributedLockLease>
{
    public void Configure(EntityTypeBuilder<DistributedLockLease> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Key).IsRequired().HasMaxLength(128);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => x.Key).IsUnique();
        builder.HasIndex(x => x.ExpiresAtUtc);
    }
}
