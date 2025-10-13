using System;
using System.Collections.Concurrent;
using ChemicalDispersionModeling.Core.Models;
using Microsoft.Extensions.Logging;

namespace ChemicalDispersionModeling.Core.Services.Implementation;

/// <summary>
/// Implements the NOAA ALOHA-compliant Gaussian plume dispersion model, which is suitable for neutrally buoyant gas releases.
/// This model is based on the formulations described in the ALOHA Technical Documentation.
/// Reference: NOAA Technical Memorandum NOS OR&R 43, Section 4.3.
/// </summary>
public class GaussianPlumeModel : IDispersionModel
{
    private readonly ILogger<GaussianPlumeModel> _logger;
    private static readonly ConcurrentDictionary<string, BriggsCoefficients> _briggsCoefficientsCache = new();

    public GaussianPlumeModel(ILogger<GaussianPlumeModel> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculates the concentration of a chemical at a specific point downwind from the release source.
    /// This core calculation is based on the ALOHA Gaussian plume formula.
    /// </summary>
    /// <param name="x">Downwind distance from the source (meters).</param>
    /// <param name="y">Crosswind distance from the plume centerline (meters).</param>
    /// <param name="z">Height above the ground (meters).</param>
    /// <param name="release">The release event parameters.</param>
    /// <param name="chemical">The chemical being released.</param>
    /// <param name="weather">The current weather conditions.</param>
    /// <returns>The calculated concentration in mg/m³.</returns>
    public double CalculateConcentration(double x, double y, double z, Release release, Chemical chemical, WeatherData weather)
    {
        // The model is not applicable for upwind, at the source, or in calm conditions.
        if (x <= 0 || weather.WindSpeed <= 0) return 0;

        var stabilityClass = weather.StabilityClass ?? "D"; // Default to neutral if not specified.
        var isUrban = DetermineUrbanRural(release.SurfaceRoughness);
        
        var (sigmaY, sigmaZ) = CalculateDispersionParameters(x, stabilityClass, isUrban);

        var Q = CalculateEffectiveReleaseRate(release, chemical, weather);
        
        var windSpeedAtReleaseHeight = CalculateWindSpeedAtHeight(weather.WindSpeed, release.ReleaseHeight, stabilityClass, release.SurfaceRoughness);

        var lateralTerm = CalculateLateralTerm(y, sigmaY);
        var verticalTerm = CalculateVerticalTerm(z, release.ReleaseHeight, sigmaZ, weather.InversionHeight);
        
        // The core Gaussian plume equation.
        var concentration = (Q / (2 * Math.PI * windSpeedAtReleaseHeight * sigmaY * sigmaZ)) * 
                           lateralTerm * verticalTerm;

        // Convert from kg/m³ to mg/m³ for typical reporting.
        return Math.Max(0, concentration * 1e6);
    }

    /// <summary>
    /// Calculates the lateral (σy) and vertical (σz) dispersion coefficients using Briggs' formulations.
    /// These coefficients determine the spread of the plume.
    /// Reference: ALOHA Technical Documentation, Section 4.3.1, Table 13.
    /// </summary>
    public (double SigmaY, double SigmaZ) CalculateDispersionParameters(double x, string stabilityClass, bool isUrban)
    {
        var xKm = x / 1000.0; // Briggs' formulas use kilometers.
        
        var coeffs = GetBriggsCoefficients(stabilityClass, isUrban);
        
        // Pasquill-Gifford-Turner (PGT) curves approximated by Briggs' formulas.
        double sigmaY = coeffs.A * Math.Pow(xKm, coeffs.B);
        double sigmaZ = coeffs.C * Math.Pow(xKm, coeffs.D) + coeffs.E;

        // Ensure a minimum dispersion to avoid singularities at the source.
        return (Math.Max(1.0, sigmaY), Math.Max(1.0, sigmaZ));
    }

    /// <summary>
    /// Calculates the lateral term of the Gaussian equation, accounting for crosswind distance.
    /// </summary>
    private static double CalculateLateralTerm(double y, double sigmaY)
    {
        return Math.Exp(-0.5 * (y * y) / (sigmaY * sigmaY));
    }

    /// <summary>
    /// Calculates the vertical term, including reflections from the ground and an optional inversion layer.
    /// Reference: ALOHA Technical Documentation, Section 4.3.
    /// </summary>
    private static double CalculateVerticalTerm(double z, double releaseHeight, double sigmaZ, double? inversionHeight)
    {
        // Standard term for reflection off the ground.
        var directTerm = Math.Exp(-0.5 * Math.Pow((z - releaseHeight) / sigmaZ, 2));
        var reflectedTerm = Math.Exp(-0.5 * Math.Pow((z + releaseHeight) / sigmaZ, 2));
        var totalVertical = directTerm + reflectedTerm;

        // If an inversion layer is present, add reflections from it.
        if (inversionHeight.HasValue && inversionHeight > 0)
        {
            double sum = 0;
            // Simplified series for trapped reflections (as per ALOHA guidance).
            for (int n = 1; n <= 4; n++)
            {
                var h1 = releaseHeight + 2 * n * inversionHeight.Value;
                var h2 = -releaseHeight + 2 * n * inversionHeight.Value;
                sum += Math.Exp(-0.5 * Math.Pow((z - h1) / sigmaZ, 2)) + Math.Exp(-0.5 * Math.Pow((z + h1) / sigmaZ, 2)) +
                       Math.Exp(-0.5 * Math.Pow((z - h2) / sigmaZ, 2)) + Math.Exp(-0.5 * Math.Pow((z + h2) / sigmaZ, 2));
            }
            totalVertical += sum;
        }

        return totalVertical;
    }

    /// <summary>
    /// Adjusts wind speed from a reference height (typically 10m) to the effective release height.
    /// Reference: ALOHA Technical Documentation, Section 4.2.3.
    /// </summary>
    private static double CalculateWindSpeedAtHeight(double windSpeedRef, double height, string stabilityClass, double roughnessLength)
    {
        const double refHeight = 10.0; // Standard reference height.
        var exponent = GetWindProfileExponent(stabilityClass, roughnessLength > 0.1);
        
        // Avoid calculation issues at or below ground level.
        if (height <= 0) return windSpeedRef;

        return windSpeedRef * Math.Pow(height / refHeight, exponent);
    }

    /// <summary>
    /// Gets the wind profile exponent based on stability class and urban/rural setting.
    /// Reference: EPA-454/R-99-005, Table 6-3.
    /// </summary>
    private static double GetWindProfileExponent(string stabilityClass, bool isUrban)
    {
        return (stabilityClass.ToUpperInvariant(), isUrban) switch
        {
            ("A", true) => 0.15, ("A", false) => 0.07,
            ("B", true) => 0.15, ("B", false) => 0.07,
            ("C", true) => 0.20, ("C", false) => 0.10,
            ("D", true) => 0.25, ("D", false) => 0.15,
            ("E", true) => 0.30, ("E", false) => 0.35,
            ("F", true) => 0.30, ("F", false) => 0.55,
            _ => 0.15 // Default to neutral rural
        };
    }

    /// <summary>
    /// Calculates the effective release rate, Q (in kg/s).
    /// </summary>
    private double CalculateEffectiveReleaseRate(Release release, Chemical chemical, WeatherData weather)
    {
        if (release.ReleaseRate.HasValue)
        {
            return release.ReleaseRate.Value;
        }
        
        if (release.TotalMass.HasValue && release.EndTime.HasValue)
        {
            var duration = (release.EndTime.Value - release.StartTime).TotalSeconds;
            return duration > 0 ? release.TotalMass.Value / duration : 0;
        }
        
        _logger.LogWarning("Release rate or mass not specified. Using default 0.1 kg/s.");
        return 0.1;
    }

    /// <summary>
    /// Determines if the area is urban or rural based on surface roughness.
    /// ALOHA uses a 20 cm threshold for roughness length.
    /// </summary>
    private static bool DetermineUrbanRural(double roughnessLength)
    {
        return roughnessLength >= 0.20;
    }

    /// <summary>
    /// Retrieves the Briggs coefficients for dispersion calculations, using a cache for efficiency.
    /// Reference: ALOHA Technical Documentation, Table 13.
    /// </summary>
    private static BriggsCoefficients GetBriggsCoefficients(string stabilityClass, bool isUrban)
    {
        var cacheKey = $"{stabilityClass}_{isUrban}";
        if (_briggsCoefficientsCache.TryGetValue(cacheKey, out var cachedCoeffs))
        {
            return cachedCoeffs;
        }

        var coeffs = (stabilityClass.ToUpperInvariant(), isUrban) switch
        {
            // Format: a, b (for σy), c, d, e (for σz)
            ("A", true)  => new BriggsCoefficients(213, 0.894, 440.8, 1.941, 9.27),
            ("A", false) => new BriggsCoefficients(213, 0.894, 459.7, 2.094, -9.6),
            ("B", true)  => new BriggsCoefficients(156, 0.894, 106.6, 1.149, 3.3),
            ("B", false) => new BriggsCoefficients(156, 0.894, 108.2, 1.098, 2.0),
            ("C", true)  => new BriggsCoefficients(104, 0.894, 61.0, 0.9146, 0),
            ("C", false) => new BriggsCoefficients(104, 0.894, 61.0, 0.9146, 0),
            ("D", true)  => new BriggsCoefficients(68, 0.894, 33.2, 0.725, -1.7),
            ("D", false) => new BriggsCoefficients(68, 0.894, 44.5, 0.516, -13.0),
            ("E", true)  => new BriggsCoefficients(50.5, 0.894, 22.8, 0.618, -1.9),
            ("E", false) => new BriggsCoefficients(50.5, 0.894, 55.4, 0.310, -34.0),
            ("F", true)  => new BriggsCoefficients(34, 0.894, 14.35, 0.740, -0.35),
            ("F", false) => new BriggsCoefficients(34, 0.894, 62.6, 0.180, -48.6),
            _ => GetBriggsCoefficients("D", isUrban) // Default to Neutral
        };
        
        _briggsCoefficientsCache.TryAdd(cacheKey, coeffs);
        return coeffs;
    }

    /// <summary>
    /// Record struct to hold the Briggs' coefficients for dispersion parameter calculations.
    /// </summary>
    private readonly record struct BriggsCoefficients(double A, double B, double C, double D, double E);
}