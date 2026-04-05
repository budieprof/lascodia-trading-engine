using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="StressTestScenario"/>. Defines table mapping,
/// column types, indexes, relationships, and the soft-delete query filter.
/// </summary>
public class StressTestScenarioConfiguration : IEntityTypeConfiguration<StressTestScenario>
{
    public void Configure(EntityTypeBuilder<StressTestScenario> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.ScenarioType).HasConversion<string>().IsRequired().HasMaxLength(30);
        builder.Property(x => x.ShockDefinitionJson).IsRequired().HasMaxLength(4000);
        builder.Property(x => x.Description).HasMaxLength(1000);

        builder.HasQueryFilter(x => !x.IsDeleted);

        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}
