using ChemicalDispersionModeling.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChemicalDispersionModeling.Data.Context.Configurations;

public class ReceptorConfiguration : IEntityTypeConfiguration<Receptor>
{
    public void Configure(EntityTypeBuilder<Receptor> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(100);
        builder.Property(e => e.ReceptorType).HasMaxLength(50);
        builder.Property(e => e.Description).HasMaxLength(500);

        // Configure relationships
        builder.HasOne(d => d.Release)
            .WithMany(p => p.Receptors)
            .HasForeignKey(d => d.ReleaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.ReleaseId);
        builder.HasIndex(e => new { e.Latitude, e.Longitude });
    }
}
