using System;
using ChemicalDispersionModeling.Core.Models;
using Microsoft.Extensions.Logging;

namespace ChemicalDispersionModeling.Core.Services.Implementation;

/// <summary>
/// Provides methods for determining atmospheric stability based on the Pasquill-Gifford-Turner classification system,
/// as implemented in NOAA's ALOHA model.
/// Reference: NOAA Technical Memorandum NOS OR&R 43, Section 4.2.
/// </summary>
public class AtmosphericStabilityService(ILogger<AtmosphericStabilityService> logger)
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double AirMolecularWeight = 28.97; // g/mol
    private const double SpecificGasConstantDryAir = 287.0; // J/kg·K

    /// <summary>
    /// Determines the Pasquill-Gifford-Turner stability class.
    /// Reference: ALOHA Technical Documentation, Section 4.2.1, Table 10.
    /// </summary>
    /// <param name="windSpeed">Wind speed in meters/second.</param>
    /// <param name="cloudCover">Cloud cover in percentage (0-100).</param>
    /// <param name="isDaytime">True if it is currently daytime.</param>
    /// <param name="solarInsolation">Optional solar insolation in W/m². If not provided, it's inferred.</param>
    /// <param name="overWater">True if the location is over a body of water.</param>
    /// <returns>A stability class from "A" to "F".</returns>
    public string DetermineStabilityClass(double windSpeed, double cloudCover, bool isDaytime, double? solarInsolation = null, bool overWater = false)
    {
        if (overWater)
        {
            // Over water, the temperature change is slow, often leading to stable conditions.
            // ALOHA defaults to D, but E is also a common recommendation for open water.
            logger.LogDebug("Over water, assuming neutral to stable conditions. Class D/E.");
            return windSpeed < 5 ? "E" : "D";
        }

        return isDaytime 
            ? DetermineDaytimeStability(windSpeed, solarInsolation ?? 0) 
            : DetermineNighttimeStability(windSpeed, cloudCover);
    }

    /// <summary>
    /// Calculates solar insolation based on the formula used in ALOHA.
    /// Reference: ALOHA Technical Documentation, Section 4.2.1.
    /// </summary>
    /// <param name="latitude">The latitude of the location.</param>
    /// <param name="dateTime">The current date and time.</param>
    /// <param name="cloudCover">The percentage of cloud cover.</param>
    /// <returns>The calculated solar insolation in W/m².</returns>
    public double CalculateSolarInsolation(double latitude, DateTime dateTime, double cloudCover)
    {
        var solarAltitude = CalculateSolarAltitude(latitude, dateTime);
        
        if (solarAltitude <= 0.1) // Sun is at or below the horizon
        {
            return 0;
        }

        // Cloudiness factor reduces insolation. 0.71 is an empirically derived coefficient.
        var cloudinessFactor = 1.0 - 0.71 * (cloudCover / 100.0); 
        var insolation = 1100 * Math.Sin(solarAltitude * DegreesToRadians) * cloudinessFactor;
        
        return Math.Max(0, insolation);
    }

    /// <summary>
    /// Calculates the solar altitude angle.
    /// Reference: ALOHA Technical Documentation, Section 4.2.1.
    /// </summary>
    private static double CalculateSolarAltitude(double latitude, DateTime dateTime)
    {
        var latRad = latitude * DegreesToRadians;
        var julianDay = dateTime.DayOfYear;
        
        // Solar declination angle calculation
        var declination = 23.45 * Math.Sin(360.0 * (284.0 + julianDay) / 365.0 * DegreesToRadians) * DegreesToRadians;
        
        // Hour angle calculation
        var hourAngle = 15.0 * (dateTime.Hour + dateTime.Minute / 60.0 - 12.0) * DegreesToRadians;
        
        // Formula for sine of solar altitude
        var sinAltitude = Math.Sin(latRad) * Math.Sin(declination) + 
                         Math.Cos(latRad) * Math.Cos(declination) * Math.Cos(hourAngle);
        
        // Return angle in degrees
        return Math.Asin(sinAltitude) / DegreesToRadians;
    }

    /// <summary>
    /// Determines daytime stability based on wind speed and solar insolation.
    /// Reference: ALOHA Technical Documentation, Table 10.
    /// </summary>
    private static string DetermineDaytimeStability(double windSpeed, double solarInsolation)
    {
        // Strong insolation: > 850 W/m²
        // Moderate insolation: > 525 W/m²
        // Slight insolation: > 175 W/m²

        if (windSpeed < 2)
        {
            if (solarInsolation > 525) return "A"; // A or A-B -> A
            if (solarInsolation > 175) return "B";
            return "D"; // Neutral/Overcast
        }
        if (windSpeed < 3)
        {
            if (solarInsolation > 850) return "A"; // A-B -> A
            if (solarInsolation > 525) return "B";
            if (solarInsolation > 175) return "C";
            return "D";
        }
        if (windSpeed < 5)
        {
            if (solarInsolation > 850) return "B";
            if (solarInsolation > 525) return "B"; // B-C -> B
            if (solarInsolation > 175) return "C";
            return "D";
        }
        if (windSpeed < 6)
        {
            if (solarInsolation > 850) return "C";
            if (solarInsolation > 525) return "C"; // C-D -> C
            return "D";
        }

        // windSpeed >= 6
        if (solarInsolation > 850) return "C";
        return "D";
    }

    /// <summary>
    /// Determines nighttime stability based on wind speed and cloud cover.
    /// Reference: ALOHA Technical Documentation, Table 10.
    /// </summary>
    private static string DetermineNighttimeStability(double windSpeed, double cloudCover)
    {
        bool isCloudy = cloudCover > 50;

        if (windSpeed < 3)
        {
            return isCloudy ? "E" : "F"; // For light winds, cloud cover is the deciding factor.
        }
        if (windSpeed < 5)
        {
            return isCloudy ? "D" : "E"; 
        }
        
        // For stronger winds (>= 5 m/s), conditions tend towards neutral regardless of cloud cover.
        return "D";
    }

    /// <summary>
    /// Gets a human-readable description for a given stability class.
    /// </summary>
    public static string GetStabilityClassDescription(string stabilityClass)
    {
        return stabilityClass?.ToUpperInvariant() switch
        {
            "A" => "Extremely Unstable",
            "B" => "Moderately Unstable",
            "C" => "Slightly Unstable",
            "D" => "Neutral",
            "E" => "Slightly Stable",
            "F" => "Moderately Stable",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Determines if a heavy gas model should be used based on relative density.
    /// Reference: ALOHA Technical Documentation, Section 4.4.1.
    /// </summary>
    public bool ShouldUseHeavyGasModel(Chemical chemical, WeatherData weather, Release release)
    {
        // For gas releases, compare molecular weight to air.
        if (release.Scenario == "Gas")
        {
            // A common heuristic is a density ratio > 1.2
            return (chemical.MolecularWeight / AirMolecularWeight) > 1.2;
        }
        
        // For liquid or other scenarios, compare the chemical's density to air density.
        var airDensity = CalculateAirDensity(weather.Temperature, weather.Pressure);
        return chemical.Density > airDensity * 1.1; // 10% denser is a common threshold
    }

    /// <summary>
    /// Calculates air density using the ideal gas law.
    /// </summary>
    /// <param name="temperatureC">Temperature in Celsius.</param>
    /// <param name="pressureMb">Pressure in millibars.</param>
    /// <returns>Air density in kg/m³.</returns>
    private static double CalculateAirDensity(double temperatureC, double pressureMb)
    { 
        var temperatureK = temperatureC + 273.15;
        var pressurePa = pressureMb * 100; // Convert millibars to Pascals
        
        return pressurePa / (SpecificGasConstantDryAir * temperatureK);
    }
}
