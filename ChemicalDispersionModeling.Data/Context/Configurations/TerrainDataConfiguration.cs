using ChemicalDispersionModeling.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChemicalDispersionModeling.Data.Context.Configurations;

public class TerrainDataConfiguration : IEntityTypeConfiguration<TerrainData>
{
    public void Configure(EntityTypeBuilder<TerrainData> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.LandUseType).HasMaxLength(50);
        builder.Property(e => e.BuildingType).HasMaxLength(50);
        builder.Property(e => e.DataSource).HasMaxLength(50);

        builder.HasIndex(e => new { e.Latitude, e.Longitude });
        builder.HasIndex(e => e.LandUseType);
    }
}
