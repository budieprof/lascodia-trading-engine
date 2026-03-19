using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class MLCausalFeatureAuditConfiguration : IEntityTypeConfiguration<MLCausalFeatureAudit>
{
    public void Configure(EntityTypeBuilder<MLCausalFeatureAudit> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.FeatureName).IsRequired().HasMaxLength(50);

        builder.Property(x => x.GrangerFStat).HasPrecision(10, 4);
        builder.Property(x => x.GrangerPValue).HasPrecision(10, 8);

        builder.HasIndex(x => new { x.MLModelId, x.FeatureIndex });
        builder.HasIndex(x => new { x.MLModelId, x.IsCausal });

        builder.HasOne(x => x.MLModel)
               .WithMany(x => x.CausalFeatureAudits)
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
