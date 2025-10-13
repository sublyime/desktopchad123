using System;
using ChemicalDispersionModeling.Core.Models;
using Microsoft.Extensions.Logging;

namespace ChemicalDispersionModeling.Core.Services.Implementation;

/// <summary>
/// Implements a heavy gas dispersion model based on the principles of the DEGADIS (Dense Gas Dispersion) model,
/// which is the basis for the ALOHA heavy gas model.
/// This model accounts for gravity-driven spreading and altered dispersion characteristics of gases denser than air.
/// Reference: NOAA Technical Memorandum NOS OR&R 43, Section 4.4.
/// </summary>
public class HeavyGasModel(ILogger<HeavyGasModel> logger) : IDispersionModel
{
    private const double GasConstant = 8.314; // J/(mol·K)
    private const double AirSpecificGasConstant = 287.0; // J/(kg·K)

    /// <summary>
    /// Calculates the concentration using the heavy gas model, transitioning from a gravity-slumping phase
    /// to a passive dispersion phase.
    /// </summary>
    public double CalculateConcentration(double x, double y, double z, Release release, Chemical chemical, WeatherData weather)
    {
        if (x <= 0 || weather.WindSpeed <= 0) return 0;

        var sourceParams = CalculateSourceParameters(release, chemical, weather);

        var transitionDistance = CalculateTransitionDistance(sourceParams, weather);

        double concentration;
        if (x < transitionDistance)
        {
            // Gravity-dominated (slumping) phase
            logger.LogDebug("Calculating concentration in gravity-dominated phase at x={X}", x);
            concentration = CalculateGravityDominatedConcentration(x, y, z, sourceParams, weather);
        }
        else
        {
            // Passive dispersion phase (behaves more like a Gaussian plume)
            logger.LogDebug("Calculating concentration in passive dispersion phase at x={X}", x);
            concentration = CalculatePassiveDiffusionConcentration(x, y, z, sourceParams, weather, transitionDistance);
        }
        
        return concentration * 1e6; // Convert kg/m³ to mg/m³
    }

    /// <summary>
    /// Calculates initial source parameters needed for the model.
    /// Reference: ALOHA Technical Documentation, Section 4.4.3.
    /// </summary>
    private HeavyGasSourceParameters CalculateSourceParameters(Release release, Chemical chemical, WeatherData weather)
    {
        var q = CalculateEffectiveReleaseRate(release);
        var airDensity = CalculateAirDensity(weather.Temperature, weather.Pressure);
        var gasDensity = CalculateGasDensity(chemical, weather.Temperature, weather.Pressure);
        
        var reducedGravity = 9.81 * (gasDensity - airDensity) / airDensity;
        
        // Volume flux of the release
        var volumeFlux = q / gasDensity;
        
        // Initial source dimensions based on volume flux and wind speed
        var initialHalfWidth = Math.Pow(volumeFlux * volumeFlux / (reducedGravity * weather.WindSpeed), 0.25);
        var initialHeight = Math.Pow(volumeFlux * reducedGravity / weather.WindSpeed, 0.25);

        return new HeavyGasSourceParameters
        {
            ReleaseRate = q,
            ReducedGravity = reducedGravity,
            InitialHalfWidth = initialHalfWidth,
            InitialHeight = initialHeight,
            GasDensity = gasDensity
        };
    }

    /// <summary>
    /// Calculates concentration in the initial, gravity-dominated slumping phase.
    /// </summary>
    private static double CalculateGravityDominatedConcentration(double x, double y, double z, HeavyGasSourceParameters source, WeatherData weather)
    {
        var windSpeed = weather.WindSpeed;

        // Cloud dimensions grow with distance
        var halfWidth = Math.Pow(source.InitialHalfWidth * source.InitialHalfWidth + 2.0 * source.ReducedGravity * source.InitialHeight * x / windSpeed, 0.5);
        var height = source.InitialHeight * source.InitialHalfWidth / halfWidth;

        // Centerline concentration at ground level
        var centerConcentration = source.ReleaseRate / (2.0 * windSpeed * halfWidth * height);

        // Assume a top-hat profile for simplicity in this phase
        var lateralFactor = Math.Abs(y) < halfWidth ? 1.0 : 0.0;
        var verticalFactor = z < height ? 1.0 : 0.0;

        return centerConcentration * lateralFactor * verticalFactor;
    }

    /// <summary>
    /// Calculates concentration in the later, passive diffusion phase, behaving like a Gaussian plume.
    /// </summary>
    private double CalculatePassiveDiffusionConcentration(double x, double y, double z, HeavyGasSourceParameters source, WeatherData weather, double transitionDistance)
    {
        // Use a virtual source to seamlessly transition from the heavy gas phase
        var (virtualSigmaY, virtualSigmaZ) = CalculateVirtualSigmas(transitionDistance, source, weather);

        var stabilityClass = weather.StabilityClass ?? "D";
        
        var (sigmaY, sigmaZ) = _gaussianModel.CalculateDispersionParameters(x, stabilityClass, false);

        // Combine virtual and actual sigmas
        var totalSigmaY = Math.Sqrt(sigmaY * sigmaY + virtualSigmaY * virtualSigmaY);
        var totalSigmaZ = Math.Sqrt(sigmaZ * sigmaZ + virtualSigmaZ * virtualSigmaZ);
        
        var windSpeedAtHeight = CalculateWindSpeedAtHeight(weather.WindSpeed);
        
        var lateralTerm = Math.Exp(-0.5 * Math.Pow(y / totalSigmaY, 2));
        var verticalTerm = Math.Exp(-0.5 * Math.Pow((z - source.InitialHeight) / totalSigmaZ, 2)) +
                           Math.Exp(-0.5 * Math.Pow((z + source.InitialHeight) / totalSigmaZ, 2));

        return (source.ReleaseRate / (2 * Math.PI * windSpeedAtHeight * totalSigmaY * totalSigmaZ)) * lateralTerm * verticalTerm;
    }

    /// <summary>
    /// Calculates the distance at which the heavy gas transitions to passive dispersion.
    /// This occurs when the bulk Richardson number is no longer large.
    /// </summary>
    private static double CalculateTransitionDistance(HeavyGasSourceParameters source, WeatherData weather)
    {
        // A common criterion for transition is when the gravity spreading velocity equals the turbulent friction velocity.
        var frictionVelocity = 0.4 * weather.WindSpeed / Math.Log(10.0 / 0.1); // Assuming 10m ref height and 0.1m roughness
        return (source.ReducedGravity * source.ReleaseRate) / (Math.Pow(frictionVelocity, 3) * source.GasDensity);
    }

    /// <summary>
    /// Calculates virtual dispersion sigmas at the transition point to ensure a smooth concentration profile.
    /// </summary>
    private static (double, double) CalculateVirtualSigmas(double transitionDistance, HeavyGasSourceParameters source, WeatherData weather)
    {
        // At the transition distance, match the heavy gas cloud dimensions to Gaussian sigmas
        var halfWidthAtTransition = Math.Pow(source.InitialHalfWidth * source.InitialHalfWidth + 2.0 * source.ReducedGravity * source.InitialHeight * transitionDistance / weather.WindSpeed, 0.5);
        var heightAtTransition = source.InitialHeight * source.InitialHalfWidth / halfWidthAtTransition;

        // Standard deviation for a top-hat profile is Width / sqrt(12)
        var virtualSigmaY = halfWidthAtTransition * 2 / Math.Sqrt(12);
        var virtualSigmaZ = heightAtTransition / Math.Sqrt(12);

        return (virtualSigmaY, virtualSigmaZ);
    }

    // Using GaussianPlumeModel's methods for standard dispersion calculations
    private readonly GaussianPlumeModel _gaussianModel = new (new LoggerFactory().CreateLogger<GaussianPlumeModel>());
    private static double CalculateWindSpeedAtHeight(double windSpeedRef) => windSpeedRef; // Simplified for now

    /// <summary>
    /// Calculates air density using the ideal gas law for dry air.
    /// </summary>
    private static double CalculateAirDensity(double temperatureC, double pressureMb)
    {
        var temperatureK = temperatureC + 273.15;
        var pressurePa = pressureMb * 100; // Convert mb to Pa
        return pressurePa / (AirSpecificGasConstant * temperatureK); // kg/m³
    }

    /// <summary>
    /// Calculates gas density using the ideal gas law.
    /// </summary>
    private static double CalculateGasDensity(Chemical chemical, double temperatureC, double pressureMb)
    {
        var temperatureK = temperatureC + 273.15;
        var pressurePa = pressureMb * 100;
        return (pressurePa * chemical.MolecularWeight / 1000.0) / (GasConstant * temperatureK); // kg/m³
    }

    /// <summary>
    /// Calculates the effective, continuous release rate in kg/s.
    /// </summary>
    private double CalculateEffectiveReleaseRate(Release release)
    {
        if (release.ReleaseRate.HasValue) return release.ReleaseRate.Value;
        if (release.TotalMass.HasValue && release.EndTime.HasValue)
        {
            var duration = (release.EndTime.Value - release.StartTime).TotalSeconds;
            return duration > 0 ? release.TotalMass.Value / duration : 0;
        }
        logger.LogWarning("Unable to determine release rate. Using a default of 0.1 kg/s.");
        return 0.1;
    }

    /// <summary>
    /// Structure to hold the key initial parameters for the heavy gas source term.
    /// </summary>
    private readonly struct HeavyGasSourceParameters
    {
        public double ReleaseRate { get; init; }      // kg/s
        public double ReducedGravity { get; init; }    // m/s²
        public double InitialHalfWidth { get; init; } // m
        public double InitialHeight { get; init; }    // m
        public double GasDensity { get; init; }       // kg/m³
    }
}
