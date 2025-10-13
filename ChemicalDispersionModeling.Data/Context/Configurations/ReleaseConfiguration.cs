using ChemicalDispersionModeling.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChemicalDispersionModeling.Data.Context.Configurations;

public class ReleaseConfiguration : IEntityTypeConfiguration<Release>
{
    public void Configure(EntityTypeBuilder<Release> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(100);
        builder.Property(e => e.ReleaseType).HasMaxLength(20);
        builder.Property(e => e.Scenario).HasMaxLength(50);
        builder.Property(e => e.ModelingStabilityClass).HasMaxLength(1);
        builder.Property(e => e.Status).HasMaxLength(20);
        builder.Property(e => e.Description).HasMaxLength(500);
        builder.Property(e => e.CreatedBy).HasMaxLength(100);

        // Configure relationships
        builder.HasOne(d => d.Chemical)
            .WithMany(p => p.Releases)
            .HasForeignKey(d => d.ChemicalId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.WeatherData)
            .WithMany(p => p.Releases)
            .HasForeignKey(d => d.WeatherDataId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(e => e.ChemicalId);
        builder.HasIndex(e => e.Status);
        builder.HasIndex(e => new { e.Latitude, e.Longitude });
    }
}
