using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChemicalDispersionModeling.Core.Models;
using ChemicalDispersionModeling.Core.Services;
using Microsoft.Extensions.Logging;

namespace ChemicalDispersionModeling.Desktop.Services
{
    /// <summary>
    /// Implementation of atmospheric dispersion modeling using Gaussian plume model
    /// </summary>
    public class DispersionModelingService : IDispersionModelingService
    {
        private readonly ILogger<DispersionModelingService> _logger;

        public DispersionModelingService(ILogger<DispersionModelingService> logger)
        {
            _logger = logger;
        }

        public async Task<IEnumerable<DispersionResult>> CalculateGaussianPlumeAsync(
            Release release, 
            IEnumerable<Receptor> receptors, 
            WeatherData weather)
        {
            return await Task.Run(() =>
            {
                try
                {
                    _logger.LogInformation($"Calculating Gaussian plume for release at {release.Latitude:F6}, {release.Longitude:F6}");
                    
                    var results = new List<DispersionResult>();
                    var effectiveReleaseRate = CalculateEffectiveReleaseRate(release, release.Chemical!, weather);
                    var plumeRise = CalculatePlumeRise(release, release.Chemical!, weather);
                    var effectiveHeight = release.ReleaseHeight + plumeRise;
                    
                    var stabilityClass = weather.StabilityClass ?? DetermineStabilityClass(weather);
                    
                    foreach (var receptor in receptors)
                    {
                        var distance = CalculateDistance(release.Latitude, release.Longitude, receptor.Latitude, receptor.Longitude);
                        
                        if (distance < 10) continue; // Skip receptors too close to avoid mathematical issues
                        
                        var (sigmaY, sigmaZ) = CalculateDispersionCoefficients(stabilityClass, distance, effectiveHeight);
                        var concentration = CalculateGaussianPlume(effectiveReleaseRate, distance, sigmaY, sigmaZ, weather.WindSpeed, effectiveHeight, receptor.Height);
                        
                        var result = new DispersionResult
                        {
                            CalculationTime = DateTime.UtcNow,
                            Latitude = receptor.Latitude,
                            Longitude = receptor.Longitude,
                            Height = receptor.Height,
                            Concentration = concentration,
                            ConcentrationUnit = "mg/m³",
                            DistanceFromSource = distance,
                            DirectionFromSource = CalculateBearing(release.Latitude, release.Longitude, receptor.Latitude, receptor.Longitude),
                            WindSpeed = weather.WindSpeed,
                            WindDirection = weather.WindDirection,
                            StabilityClass = stabilityClass,
                            Temperature = weather.Temperature,
                            RiskLevel = DetermineRiskLevel(concentration, release.Chemical!),
                            ModelUsed = "Gaussian Plume",
                            ReleaseId = release.Id,
                            ReceptorId = receptor.Id,
                            Release = release,
                            Receptor = receptor
                        };
                        
                        results.Add(result);
                    }
                    
                    _logger.LogInformation($"Calculated concentrations for {results.Count} receptors");
                    return results;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error calculating Gaussian plume");
                    throw;
                }
            });
        }

        public async Task<IEnumerable<DispersionResult>> CalculateDispersionGridAsync(
            Release release, 
            WeatherData weather, 
            double gridSize = 100, 
            double maxDistance = 10000)
        {
            return await Task.Run(() =>
            {
                try
                {
                    _logger.LogInformation($"Calculating dispersion grid with {gridSize}m resolution, max distance {maxDistance}m");
                    
                    var results = new List<DispersionResult>();
                    var effectiveReleaseRate = CalculateEffectiveReleaseRate(release, release.Chemical!, weather);
                    var plumeRise = CalculatePlumeRise(release, release.Chemical!, weather);
                    var effectiveHeight = release.ReleaseHeight + plumeRise;
                    var stabilityClass = weather.StabilityClass ?? DetermineStabilityClass(weather);
                    
                    // Convert wind direction to radians for calculations
                    var windDirRad = weather.WindDirection * Math.PI / 180.0;
                    
                    // Create grid points - focus more points downwind
                    var gridPoints = GenerateGridPoints(release.Latitude, release.Longitude, gridSize, maxDistance, windDirRad);
                    
                    foreach (var point in gridPoints)
                    {
                        var distance = CalculateDistance(release.Latitude, release.Longitude, point.lat, point.lng);
                        
                        if (distance < 10) continue; // Skip points too close
                        
                        var (sigmaY, sigmaZ) = CalculateDispersionCoefficients(stabilityClass, distance, effectiveHeight);
                        var concentration = CalculateGaussianPlume(effectiveReleaseRate, distance, sigmaY, sigmaZ, weather.WindSpeed, effectiveHeight, 0, point.crosswindDistance);
                        
                        // Only include points with significant concentration
                        if (concentration > 0.001) // 0.001 mg/m³ threshold
                        {
                            var result = new DispersionResult
                            {
                                CalculationTime = DateTime.UtcNow,
                                Latitude = point.lat,
                                Longitude = point.lng,
                                Height = 0,
                                Concentration = concentration,
                                ConcentrationUnit = "mg/m³",
                                DistanceFromSource = distance,
                                DirectionFromSource = CalculateBearing(release.Latitude, release.Longitude, point.lat, point.lng),
                                WindSpeed = weather.WindSpeed,
                                WindDirection = weather.WindDirection,
                                StabilityClass = stabilityClass,
                                Temperature = weather.Temperature,
                                RiskLevel = DetermineRiskLevel(concentration, release.Chemical!),
                                ModelUsed = "Gaussian Plume Grid",
                                ReleaseId = release.Id
                            };
                            
                            results.Add(result);
                        }
                    }
                    
                    _logger.LogInformation($"Generated {results.Count} grid points with significant concentrations");
                    return results;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error calculating dispersion grid");
                    throw;
                }
            });
        }

        public async Task<IEnumerable<DispersionResult>> CalculateTimeVaryingDispersionAsync(
            Release release, 
            IEnumerable<Receptor> receptors, 
            IEnumerable<WeatherData> weatherSequence)
        {
            var allResults = new List<DispersionResult>();
            
            foreach (var weather in weatherSequence)
            {
                var timeResults = await CalculateGaussianPlumeAsync(release, receptors, weather);
                allResults.AddRange(timeResults);
            }
            
            return allResults;
        }

        public async Task<IEnumerable<DispersionResult>> RunSimulationAsync(
            Release release, 
            Chemical chemical, 
            WeatherData weather, 
            IEnumerable<Receptor> receptors)
        {
            release.Chemical = chemical;
            return await CalculateGaussianPlumeAsync(release, receptors, weather);
        }

        public async Task<double> CalculateDoseAsync(
            double x, 
            double y, 
            double z, 
            Release release, 
            Chemical chemical, 
            WeatherData weather, 
            TimeSpan exposureTime)
        {
            var distance = CalculateDistance(release.Latitude, release.Longitude, x, y);
            var stabilityClass = weather.StabilityClass ?? DetermineStabilityClass(weather);
            var effectiveReleaseRate = CalculateEffectiveReleaseRate(release, chemical, weather);
            var plumeRise = CalculatePlumeRise(release, chemical, weather);
            var effectiveHeight = release.ReleaseHeight + plumeRise;
            
            var (sigmaY, sigmaZ) = CalculateDispersionCoefficients(stabilityClass, distance, effectiveHeight);
            var concentration = CalculateGaussianPlume(effectiveReleaseRate, distance, sigmaY, sigmaZ, weather.WindSpeed, effectiveHeight, z);
            
            var dose = concentration * exposureTime.TotalSeconds; // mg·s/m³
            
            return await Task.FromResult(dose);
        }

        public async Task<(double distance, double concentration)> GetMaximumConcentrationAsync(
            Release release, 
            Chemical chemical, 
            WeatherData weather, 
            double maxDistance = 10000)
        {
            var stabilityClass = weather.StabilityClass ?? DetermineStabilityClass(weather);
            var effectiveReleaseRate = CalculateEffectiveReleaseRate(release, chemical, weather);
            var plumeRise = CalculatePlumeRise(release, chemical, weather);
            var effectiveHeight = release.ReleaseHeight + plumeRise;
            
            double maxConcentration = 0;
            double maxDistance_found = 0;
            
            // Search for maximum concentration along centerline
            for (double distance = 50; distance <= maxDistance; distance += 50)
            {
                var (sigmaY, sigmaZ) = CalculateDispersionCoefficients(stabilityClass, distance, effectiveHeight);
                var concentration = CalculateGaussianPlume(effectiveReleaseRate, distance, sigmaY, sigmaZ, weather.WindSpeed, effectiveHeight, 0);
                
                if (concentration > maxConcentration)
                {
                    maxConcentration = concentration;
                    maxDistance_found = distance;
                }
            }
            
            return await Task.FromResult((maxDistance_found, maxConcentration));
        }

        public double CalculateEffectiveReleaseRate(Release release, Chemical chemical, WeatherData weather)
        {
            double effectiveRate = release.ReleaseRate ?? 1.0;
            
            switch (release.ReleaseType?.ToLower())
            {
                case "instantaneous":
                    effectiveRate = (release.TotalMass ?? 100) / 60.0;
                    break;
                case "continuous":
                    effectiveRate = release.ReleaseRate ?? 1.0;
                    break;
                case "evaporating":
                    var poolArea = Math.PI * Math.Pow((release.DiameterOrArea ?? 10) / 2, 2);
                    effectiveRate = CalculateEvaporationRate(chemical, weather, poolArea);
                    break;
            }
            
            return Math.Max(effectiveRate, 0);
        }

        public double CalculatePlumeRise(Release release, Chemical chemical, WeatherData weather)
        {
            double buoyancyFlux = 0;
            double momentumFlux = 0;
            
            var stackDiameter = release.DiameterOrArea ?? 1.0;
            var exitVelocity = 10.0;
            
            momentumFlux = exitVelocity * stackDiameter * stackDiameter * Math.PI / 4;
            
            var releaseTemp = release.InitialTemperature ?? weather.Temperature;
            if (releaseTemp > weather.Temperature)
            {
                var deltaT = releaseTemp - weather.Temperature;
                buoyancyFlux = 9.81 * (release.ReleaseRate ?? 1.0) * deltaT / (weather.Temperature + 273.15);
            }
            
            double plumeRise = 0;
            if (weather.WindSpeed > 0)
            {
                plumeRise = 1.6 * Math.Pow(buoyancyFlux, 1.0/3.0) * Math.Pow(weather.WindSpeed, -1.0/3.0);
                plumeRise += momentumFlux / (Math.PI * weather.WindSpeed);
            }
            
            return Math.Max(plumeRise, 0);
        }

        public (double SigmaY, double SigmaZ) CalculateDispersionCoefficients(string stabilityClass, double distance, double releaseHeight)
        {
            var x = distance / 1000.0; // Convert to km
            
            double sigmaY, sigmaZ;
            
            switch (stabilityClass.ToUpper())
            {
                case "A": // Very unstable
                    sigmaY = 0.22 * x * Math.Pow(1 + 0.0001 * distance, -0.5);
                    sigmaZ = 0.20 * x;
                    break;
                case "B": // Moderately unstable
                    sigmaY = 0.16 * x * Math.Pow(1 + 0.0001 * distance, -0.5);
                    sigmaZ = 0.12 * x;
                    break;
                case "C": // Slightly unstable
                    sigmaY = 0.11 * x * Math.Pow(1 + 0.0001 * distance, -0.5);
                    sigmaZ = 0.08 * x * Math.Pow(1 + 0.0002 * distance, -0.5);
                    break;
                case "D": // Neutral
                    sigmaY = 0.08 * x * Math.Pow(1 + 0.0001 * distance, -0.5);
                    sigmaZ = 0.06 * x * Math.Pow(1 + 0.0015 * distance, -0.5);
                    break;
                case "E": // Slightly stable
                    sigmaY = 0.06 * x * Math.Pow(1 + 0.0001 * distance, -0.5);
                    sigmaZ = 0.03 * x * Math.Pow(1 + 0.0003 * distance, -1);
                    break;
                case "F": // Moderately stable
                    sigmaY = 0.04 * x * Math.Pow(1 + 0.0001 * distance, -0.5);
                    sigmaZ = 0.016 * x * Math.Pow(1 + 0.0003 * distance, -1);
                    break;
                default:
                    sigmaY = 0.08 * x * Math.Pow(1 + 0.0001 * distance, -0.5);
                    sigmaZ = 0.06 * x * Math.Pow(1 + 0.0015 * distance, -0.5);
                    break;
            }
            
            if (releaseHeight < 100)
            {
                sigmaZ = Math.Min(sigmaZ * 1000, releaseHeight * 0.8);
            }
            else
            {
                sigmaZ = sigmaZ * 1000;
            }
            
            return (Math.Max(1, sigmaY * 1000), Math.Max(1, sigmaZ));
        }

        public string DetermineRiskLevel(double concentration, Chemical chemical)
        {
            if (chemical.ToxicityThreshold == null) return "Unknown";
            
            var threshold = chemical.ToxicityThreshold.Value;
            var ratio = concentration / threshold;
            
            if (ratio < 0.01) return "Minimal";
            if (ratio < 0.1) return "Low";
            if (ratio < 0.5) return "Medium";
            if (ratio < 1.0) return "High";
            return "Critical";
        }

        private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
        {
            var R = 6371000; // Earth's radius in meters
            var dLat = (lat2 - lat1) * Math.PI / 180;
            var dLon = (lon2 - lon1) * Math.PI / 180;
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private double CalculateBearing(double lat1, double lon1, double lat2, double lon2)
        {
            var dLon = (lon2 - lon1) * Math.PI / 180;
            var y = Math.Sin(dLon) * Math.Cos(lat2 * Math.PI / 180);
            var x = Math.Cos(lat1 * Math.PI / 180) * Math.Sin(lat2 * Math.PI / 180) -
                    Math.Sin(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Cos(dLon);
            var bearing = Math.Atan2(y, x) * 180 / Math.PI;
            return (bearing + 360) % 360;
        }

        private double CalculateGaussianPlume(
            double releaseRate, 
            double distance, 
            double sigmaY, 
            double sigmaZ, 
            double windSpeed, 
            double releaseHeight, 
            double receptorHeight,
            double crosswindDistance = 0)
        {
            if (distance == 0 || windSpeed == 0) return 0;
            
            var crosswindTerm = Math.Exp(-0.5 * Math.Pow(crosswindDistance / sigmaY, 2));
            
            var heightTerm = Math.Exp(-0.5 * Math.Pow((receptorHeight - releaseHeight) / sigmaZ, 2)) +
                            Math.Exp(-0.5 * Math.Pow((receptorHeight + releaseHeight) / sigmaZ, 2));
            
            var concentration = (releaseRate / (2 * Math.PI * sigmaY * sigmaZ * windSpeed)) * crosswindTerm * heightTerm;
            
            return Math.Max(0, concentration * 1000); // Convert to mg/m³
        }

        private string DetermineStabilityClass(WeatherData weather)
        {
            var windSpeed = weather.WindSpeed;
            var timeOfDay = DateTime.Now.Hour;
            var isDay = timeOfDay >= 6 && timeOfDay <= 18;
            var cloudCover = weather.CloudCover ?? 0.5;
            
            if (isDay && cloudCover < 0.3 && windSpeed < 2) return "A";
            if (isDay && cloudCover < 0.5 && windSpeed < 3) return "B";
            if (isDay && windSpeed < 5) return "C";
            if (windSpeed < 6) return "D";
            if (!isDay && windSpeed < 3) return "F";
            return "E";
        }

        private double CalculateEvaporationRate(Chemical chemical, WeatherData weather, double poolArea)
        {
            var tempK = weather.Temperature + 273.15;
            var vaporPressure = (chemical.VaporPressure ?? 1000) * Math.Exp(-5000 / (8.314 * tempK));
            var evapRate = poolArea * vaporPressure * Math.Sqrt(chemical.MolecularWeight / (2 * Math.PI * 8.314 * tempK));
            evapRate *= (1 + 0.1 * weather.WindSpeed);
            return evapRate * 1000; // Convert to mg/s
        }

        private List<(double lat, double lng, double crosswindDistance)> GenerateGridPoints(
            double centerLat, 
            double centerLng, 
            double gridSize, 
            double maxDistance, 
            double windDirRad)
        {
            var points = new List<(double lat, double lng, double crosswindDistance)>();
            
            var latStep = gridSize / 111000.0;
            var lngStep = gridSize / (111000.0 * Math.Cos(centerLat * Math.PI / 180));
            
            var maxSteps = (int)(maxDistance / gridSize);
            
            for (int i = -maxSteps; i <= maxSteps; i++)
            {
                for (int j = -maxSteps; j <= maxSteps; j++)
                {
                    var lat = centerLat + i * latStep;
                    var lng = centerLng + j * lngStep;
                    
                    var distance = CalculateDistance(centerLat, centerLng, lat, lng);
                    if (distance <= maxDistance)
                    {
                        var crosswind = Math.Abs(j * gridSize);
                        points.Add((lat, lng, crosswind));
                    }
                }
            }
            
            return points;
        }
    }
}