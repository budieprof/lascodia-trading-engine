using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class MLCorrelatedFailureLogConfiguration : IEntityTypeConfiguration<MLCorrelatedFailureLog>
{
    public void Configure(EntityTypeBuilder<MLCorrelatedFailureLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.SymbolsAffectedJson).HasColumnType("text");

        builder.HasIndex(x => x.DetectedAt);

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
