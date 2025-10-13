using ChemicalDispersionModeling.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChemicalDispersionModeling.Data.Context.Configurations;

public class ChemicalConfiguration : IEntityTypeConfiguration<Chemical>
{
    public void Configure(EntityTypeBuilder<Chemical> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(100);
        builder.Property(e => e.CasNumber).HasMaxLength(50);
        builder.Property(e => e.PhysicalState).HasMaxLength(20);
        builder.Property(e => e.ToxicityUnit).HasMaxLength(20);
        builder.Property(e => e.Description).HasMaxLength(500);

        builder.HasIndex(e => e.Name);
        builder.HasIndex(e => e.CasNumber);
    }
}
