using ChemicalDispersionModeling.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChemicalDispersionModeling.Data.Context.Configurations;

public class DispersionResultConfiguration : IEntityTypeConfiguration<DispersionResult>
{
    public void Configure(EntityTypeBuilder<DispersionResult> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ConcentrationUnit).HasMaxLength(10);
        builder.Property(e => e.StabilityClass).HasMaxLength(1);
        builder.Property(e => e.RiskLevel).HasMaxLength(20);
        builder.Property(e => e.ModelUsed).HasMaxLength(50);

        // Configure relationships
        builder.HasOne(d => d.Release)
            .WithMany(p => p.DispersionResults)
            .HasForeignKey(d => d.ReleaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(d => d.Receptor)
            .WithMany(p => p.DispersionResults)
            .HasForeignKey(d => d.ReceptorId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(e => e.ReleaseId);
        builder.HasIndex(e => e.ReceptorId);
        builder.HasIndex(e => e.CalculationTime);
        builder.HasIndex(e => new { e.Latitude, e.Longitude });
    }
}
