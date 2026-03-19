using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Infrastructure.Persistence.Configurations;

public class MLConformalCalibrationConfiguration : IEntityTypeConfiguration<MLConformalCalibration>
{
    public void Configure(EntityTypeBuilder<MLConformalCalibration> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Timeframe).HasConversion<string>().IsRequired().HasMaxLength(10);
        builder.Property(x => x.NonConformityScoresJson).IsRequired();
        builder.Property(x => x.CoverageAlpha).HasPrecision(5, 4);
        builder.Property(x => x.CoverageThreshold).HasPrecision(10, 8);
        builder.Property(x => x.EmpiricalCoverage).HasPrecision(5, 4);
        builder.Property(x => x.AmbiguousRate).HasPrecision(5, 4);

        builder.HasIndex(x => new { x.MLModelId, x.IsDeleted });
        builder.HasIndex(x => new { x.Symbol, x.Timeframe });

        builder.HasOne(x => x.MLModel)
               .WithMany(x => x.ConformalCalibrations)
               .HasForeignKey(x => x.MLModelId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
