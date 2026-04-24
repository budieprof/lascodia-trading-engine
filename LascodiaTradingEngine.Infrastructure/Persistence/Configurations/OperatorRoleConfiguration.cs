using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="OperatorRole"/>. The <c>(TradingAccountId, Role)</c>
/// filtered unique index prevents duplicate live grants while letting historical revocations
/// (soft-deleted) coexist on the same pair.
/// </summary>
public class OperatorRoleConfiguration : IEntityTypeConfiguration<OperatorRole>
{
    public void Configure(EntityTypeBuilder<OperatorRole> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Role).IsRequired().HasMaxLength(50);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.TradingAccountId, x.Role })
            .IsUnique()
            .HasFilter("\"IsDeleted\" = FALSE");

        builder.HasIndex(x => x.TradingAccountId);
    }
}
