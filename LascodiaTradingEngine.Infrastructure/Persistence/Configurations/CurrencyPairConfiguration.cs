using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class CurrencyPairConfiguration : IEntityTypeConfiguration<CurrencyPair>
{
    public void Configure(EntityTypeBuilder<CurrencyPair> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.BaseCurrency).IsRequired().HasMaxLength(3);
        builder.Property(x => x.QuoteCurrency).IsRequired().HasMaxLength(3);
        builder.Property(x => x.ContractSize).HasPrecision(18, 5);
        builder.Property(x => x.MinLotSize).HasPrecision(18, 5);
        builder.Property(x => x.MaxLotSize).HasPrecision(18, 5);
        builder.Property(x => x.LotStep).HasPrecision(18, 5);

        builder.HasIndex(x => x.Symbol).IsUnique();
    }
}
