using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChemicalDispersionModeling.Core.Models;
using Microsoft.Extensions.Logging;

namespace ChemicalDispersionModeling.Core.Services.Implementation;

public partial class DispersionModelingService(
    ILogger<DispersionModelingService> logger,
    GaussianPlumeModel gaussianModel,
    HeavyGasModel heavyGasModel,
    AtmosphericStabilityService stabilityService)
    : IDispersionModelingService
{
    public async Task<IEnumerable<DispersionResult>> RunSimulationAsync(
        Release release, 
        Chemical chemical, 
        WeatherData weather, 
        IEnumerable<Receptor> receptors)
    {
        logger.LogInformation("Starting ALOHA-compliant dispersion simulation for chemical {ChemicalName}", chemical.Name);

        var weatherData = EnsureStabilityClass(weather);
        var receptorList = receptors.ToList();

        var calculationTasks = receptorList.Select(async receptor =>
        {
            var concentration = await CalculateConcentrationAsync(receptor.Latitude, receptor.Longitude, receptor.Elevation, release, chemical, weatherData);
            return CreateDispersionResult(release, chemical, weatherData, receptor, concentration);
        });

        var results = await Task.WhenAll(calculationTasks);

        logger.LogInformation("Completed simulation for {ReceptorCount} receptors", receptorList.Count);
        return results;
    }

    public Task<double> CalculateConcentrationAsync(double x, double y, double z, Release release, Chemical chemical, WeatherData weather)
    {
        IDispersionModel model = IsHeavyGas(chemical) ? heavyGasModel : gaussianModel;
        logger.LogDebug("Using {ModelName} model for {ChemicalName}", model.GetType().Name, chemical.Name);
        return Task.FromResult(model.CalculateConcentration(x, y, z, release, chemical, weather));
    }

    private static bool IsHeavyGas(Chemical chemical)
    {
        if (chemical.MolecularWeight <= 0) return false;
        const double airMolecularWeight = 28.97; // g/mol
        return (chemical.MolecularWeight / airMolecularWeight) > 1.2;
    }

    private static string GetModelName(Chemical chemical) => IsHeavyGas(chemical) ? "ALOHA Heavy Gas (DEGADIS)" : "ALOHA Gaussian Plume";

    private static double CalculateSolarRadiation(DateTime dateTime)
    {
        var hour = dateTime.Hour;
        if (hour < 6 || hour > 18) return 0;

        var solarAngle = Math.Sin((hour - 6) * Math.PI / 12);
        var seasonalFactor = 1.0 + 0.3 * Math.Cos(2 * Math.PI * (dateTime.DayOfYear - 172) / 365.25);
        return Math.Max(0, 1000 * solarAngle * seasonalFactor); // W/m²
    }

    public async Task<double> CalculateDoseAsync(double x, double y, double z, Release release, Chemical chemical, WeatherData weather, TimeSpan exposureTime)
    {
        var concentration = await CalculateConcentrationAsync(x, y, z, release, chemical, weather);
        var duration = exposureTime.TotalSeconds;
        if (release.ReleaseType == "Continuous" && release.StartTime.HasValue && release.EndTime.HasValue)
        {
            var releaseDuration = (release.EndTime.Value - release.StartTime.Value).TotalSeconds;
            duration = Math.Min(duration, releaseDuration);
        }

        return concentration * (duration / 3600.0); // mg·h/m³
    }

    public async Task<(double distance, double concentration)> GetMaximumConcentrationAsync(Release release, Chemical chemical, WeatherData weather, double maxDistance = 10000)
    {
        const int steps = 100;
        var stepSize = maxDistance / steps;
        var distances = Enumerable.Range(1, steps).Select(i => i * stepSize);

        var concentrationTasks = distances.Select(async x => new 
        {
            Distance = x,
            Concentration = await CalculateConcentrationAsync(x, 0, 0, release, chemical, weather)
        });

        var concentrations = await Task.WhenAll(concentrationTasks);

        if (concentrations.Length == 0)
        {
            return (0, 0);
        }

        var maxResult = concentrations.OrderByDescending(c => c.Concentration).First();
        
        return (maxResult.Distance, maxResult.Concentration);
    }

    public Task<IEnumerable<DispersionResult>> CalculateGaussianPlumeAsync(Release release, IEnumerable<Receptor> receptors, WeatherData weather) =>
        RunSimulationAsync(release, GetDefaultChemical(), weather, receptors);

    public Task<IEnumerable<DispersionResult>> CalculateDispersionGridAsync(Release release, WeatherData weather, double gridSize = 100, double maxDistance = 10000)
    {
        var receptors = GenerateGridReceptors(0, 0, gridSize, maxDistance);
        return RunSimulationAsync(release, GetDefaultChemical(), weather, receptors);
    }

    public async Task<IEnumerable<DispersionResult>> CalculateTimeVaryingDispersionAsync(Release release, IEnumerable<Receptor> receptors, IEnumerable<WeatherData> weatherSequence)
    {
        var defaultChemical = GetDefaultChemical();
        var receptorList = receptors.ToList();
        
        var simulationTasks = weatherSequence.Select(weather => RunSimulationAsync(release, defaultChemical, weather, receptorList));
        
        var allResults = await Task.WhenAll(simulationTasks);
        
        return allResults.SelectMany(r => r);
    }

    public double CalculateEffectiveReleaseRate(Release release, Chemical chemical, WeatherData weather)
    {
        if (release.ReleaseRate.HasValue) return release.ReleaseRate.Value;
        if (release.TotalMass.HasValue && release.EndTime.HasValue && release.StartTime.HasValue)
        {
            var duration = (release.EndTime.Value - release.StartTime.Value).TotalSeconds;
            return duration > 0 ? release.TotalMass.Value / duration : 0;
        }
        return 0.1; // Default small release
    }

    public double CalculatePlumeRise(Release release, Chemical chemical, WeatherData weather)
    {
        var windSpeed = Math.Max(weather.WindSpeed, 1.0);
        var buoyancyFlux = CalculateBuoyancyFlux(release, chemical, weather);

        return buoyancyFlux > 0
            ? 1.6 * Math.Pow(buoyancyFlux / windSpeed, 1.0 / 3.0) * Math.Pow(1000, 2.0 / 3.0) // Buoyant
            : 3.0 * CalculateMomentumFlux(release) / windSpeed; // Momentum
    }

    public (double SigmaY, double SigmaZ) CalculateDispersionCoefficients(string stabilityClass, double distance, double releaseHeight)
    {
        var upperStabilityClass = stabilityClass.ToUpperInvariant();
        return (CalculateSimpleSigmaY(distance, upperStabilityClass), CalculateSimpleSigmaZ(distance, upperStabilityClass));
    }

    public string DetermineRiskLevel(double concentration, Chemical chemical)
    {
        const double lc50 = 1000.0; // Default LC50 in mg/m³
        const double aegl3 = lc50;
        const double aegl2 = aegl3 * 0.1;
        const double aegl1 = aegl3 * 0.01;

        return concentration switch
        {
            >= aegl3 => "Life Threatening",
            >= aegl2 => "Disabling",
            >= aegl1 => "Notable Discomfort",
            > 0.1 => "Detectable",
            _ => "Safe"
        };
    }

    private static List<Receptor> GenerateGridReceptors(double centerX, double centerY, double gridSize, double maxDistance)
    {
        var receptors = new List<Receptor>();
        var id = 1;
        for (var x = -maxDistance; x <= maxDistance; x += gridSize)
        {
            for (var y = -maxDistance; y <= maxDistance; y += gridSize)
            {
                if (x * x + y * y <= maxDistance * maxDistance)
                {
                    receptors.Add(new Receptor { Id = id++, Name = $"Grid_{x:F0}_{y:F0}", Latitude = centerX + x, Longitude = centerY + y, Elevation = 1.5, ReceptorType = "Grid Point" });
                }
            }
        }
        return receptors;
    }

    private static Chemical GetDefaultChemical() => new() { Name = "Generic Chemical", MolecularWeight = 58.12, VaporPressure = 24600, BoilingPoint = 56.05 };

    private double CalculateBuoyancyFlux(Release release, Chemical chemical, WeatherData weather) =>
        9.81 * CalculateEffectiveReleaseRate(release, chemical, weather) * ((release.InitialTemperature ?? weather.Temperature) - weather.Temperature) / (weather.Temperature + 273.15);

    private static double CalculateMomentumFlux(Release release)
    {
        const double exitVelocity = 1.0;
        var stackDiameter = release.DiameterOrArea ?? 1.0;
        var stackArea = Math.PI * Math.Pow(stackDiameter / 2, 2);
        return exitVelocity * exitVelocity * stackArea;
    }

    private static double CalculateSimpleSigmaY(double distance, string stabilityClass)
    {
        var xKm = distance / 1000.0;
        var factor = stabilityClass switch
        {
            "A" => 0.22,
            "B" => 0.16,
            "C" => 0.11,
            "D" => 0.08,
            "E" => 0.06,
            "F" => 0.04,
            _ => 0.08
        };
        return factor * xKm / Math.Pow(1 + 0.0001 * distance, 0.5) * 1000;
    }

    private static double CalculateSimpleSigmaZ(double distance, string stabilityClass)
    {
        var xKm = distance / 1000.0;
        var (factor, power) = stabilityClass switch
        {
            "A" => (0.2, 1.0),
            "B" => (0.12, 1.0),
            "C" => (0.08, 0.5),
            "D" => (0.06, 0.5),
            "E" => (0.03, 1.0),
            "F" => (0.016, 1.0),
            _ => (0.06, 0.5)
        };
        return factor * xKm / Math.Pow(1 + (stabilityClass[0] < 'E' ? 0.0002 : 0.0015) * distance, power) * 1000;
    }

    private WeatherData EnsureStabilityClass(WeatherData weather)
    {
        if (!string.IsNullOrEmpty(weather.StabilityClass)) return weather;

        var isDaytime = DateTime.Now.Hour >= 6 && DateTime.Now.Hour <= 18;
        weather.StabilityClass = stabilityService.DetermineStabilityClass(
            weather.WindSpeed,
            weather.CloudCover ?? 5,
            isDaytime,
            weather.SolarRadiation ?? CalculateSolarRadiation(DateTime.Now));

        logger.LogDebug("Determined atmospheric stability class: {StabilityClass}", weather.StabilityClass);
        return weather;
    }

    private DispersionResult CreateDispersionResult(Release release, Chemical chemical, WeatherData weather, Receptor receptor, double concentration) =>
        new() {
            ReleaseId = release.Id,
            ReceptorId = receptor.Id,
            Latitude = receptor.Latitude,
            Longitude = receptor.Longitude,
            Height = receptor.Elevation,
            Concentration = concentration,
            ConcentrationUnit = "mg/m³",
            ModelUsed = GetModelName(chemical),
            CalculationTime = DateTime.UtcNow,
            WindSpeed = weather.WindSpeed,
            WindDirection = weather.WindDirection,
            StabilityClass = weather.StabilityClass ?? "D",
            Temperature = weather.Temperature,
            RiskLevel = DetermineRiskLevel(concentration, chemical)
        };
}

public interface IDispersionModel
{
    double CalculateConcentration(double x, double y, double z, Release release, Chemical chemical, WeatherData weather);
}
