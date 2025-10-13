using ChemicalDispersionModeling.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChemicalDispersionModeling.Data.Context.Configurations;

public class WeatherDataConfiguration : IEntityTypeConfiguration<WeatherData>
{
    public void Configure(EntityTypeBuilder<WeatherData> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.StationId).IsRequired().HasMaxLength(50);
        builder.Property(e => e.StabilityClass).HasMaxLength(1);
        builder.Property(e => e.Source).HasMaxLength(50);

        builder.HasIndex(e => new { e.StationId, e.Timestamp });
        builder.HasIndex(e => new { e.Latitude, e.Longitude });
    }
}
