using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="RevokedToken"/>. The <c>Jti</c> column carries a
/// unique index so revocation lookups are an indexed seek, and the
/// <c>(TradingAccountId, ExpiresAt)</c> covering index supports both account-scoped revoke-all
/// scans and the daily expired-row cleanup job.
/// </summary>
public class RevokedTokenConfiguration : IEntityTypeConfiguration<RevokedToken>
{
    public void Configure(EntityTypeBuilder<RevokedToken> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Jti).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Reason).HasMaxLength(200);

        builder.HasIndex(x => x.Jti).IsUnique();
        builder.HasIndex(x => new { x.TradingAccountId, x.ExpiresAt });
    }
}
