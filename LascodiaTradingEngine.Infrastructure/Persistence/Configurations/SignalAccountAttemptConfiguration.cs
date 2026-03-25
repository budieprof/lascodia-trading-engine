using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class SignalAccountAttemptConfiguration : IEntityTypeConfiguration<SignalAccountAttempt>
{
    public void Configure(EntityTypeBuilder<SignalAccountAttempt> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.BlockReason)
            .HasMaxLength(1000);

        builder.HasOne(x => x.TradeSignal)
            .WithMany(s => s.AccountAttempts)
            .HasForeignKey(x => x.TradeSignalId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.TradingAccount)
            .WithMany()
            .HasForeignKey(x => x.TradingAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.HasIndex(x => new { x.TradeSignalId, x.TradingAccountId });
    }
}
