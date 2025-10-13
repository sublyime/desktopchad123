using System.Reflection;
using ChemicalDispersionModeling.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ChemicalDispersionModeling.Data.Context;

/// <summary>
/// Entity Framework DbContext for Chemical Dispersion Modeling application
/// </summary>
public class ChemicalDispersionContext : DbContext
{
    public ChemicalDispersionContext(DbContextOptions<ChemicalDispersionContext> options)
        : base(options)
    {
    }

    // DbSets for all entities
    public DbSet<Chemical> Chemicals { get; set; }
    public DbSet<WeatherData> WeatherData { get; set; }
    public DbSet<Release> Releases { get; set; }
    public DbSet<Receptor> Receptors { get; set; }
    public DbSet<DispersionResult> DispersionResults { get; set; }
    public DbSet<TerrainData> TerrainData { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<double>()
            .HavePrecision(18, 6);

        configurationBuilder.Properties<double>(p => p.Name.Contains("Latitude") || p.Name.Contains("Longitude"))
            .HavePrecision(10, 7);
    }
}
