using System;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ChemicalDispersionModeling.Core.Models;
using ChemicalDispersionModeling.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ChemicalDispersionModeling.Desktop.Services
{
    public class RealMappingService : IRealMappingService
    {
        private readonly ILogger<RealMappingService> _logger;
        private WebView2? _webView;
        private MapProvider _currentProvider;
        private string _apiKey = "";
        private double _currentLatitude;
        private double _currentLongitude;
        private int _currentZoom = 10;
        private readonly List<string> _dispersionOverlays = new();
        private bool _isReady = false;

        public event EventHandler<MapClickEventArgs>? MapClicked;
        public event EventHandler<MapViewChangedEventArgs>? MapViewChanged;

        public RealMappingService(ILogger<RealMappingService> logger)
        {
            _logger = logger;
        }

        public void SetWebView(WebView2 webView)
        {
            _webView = webView;
            _webView.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2InitializationCompleted += OnCoreWebView2InitializationCompleted;

            Console.WriteLine("=== SetWebView called ===");
            Console.WriteLine($"CoreWebView2 already initialized: {_webView.CoreWebView2 != null}");

            if (_webView.CoreWebView2 != null)
            {
                Console.WriteLine("=== CoreWebView2 already ready, attaching handler manually ===");
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                Console.WriteLine("=== C# WebMessageReceived handler attached manually! ===");
            }
        }

        public async Task<bool> InitializeAsync(MapProvider provider, string apiKey = "")
        {
            try
            {
                _logger.LogInformation($"Initializing real mapping service with {provider}");
                Console.WriteLine($"=== InitializeAsync called ===");
                Console.WriteLine($"CoreWebView2 before EnsureCoreWebView2Async: {(_webView != null && _webView.CoreWebView2 != null)}");

                if (_webView != null)
                {
                    await _webView.EnsureCoreWebView2Async();
                }
                Console.WriteLine($"CoreWebView2 after EnsureCoreWebView2Async: {(_webView != null && _webView.CoreWebView2 != null)}");

                if (_webView != null && _webView.CoreWebView2 != null)
                {
                    Console.WriteLine("=== Double-checking WebMessageReceived handler after EnsureCoreWebView2Async ===");
                    _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                    Console.WriteLine("=== C# WebMessageReceived handler attached/re-attached! ===");
                }

                var htmlContent = provider switch
                {
                    MapProvider.OpenStreetMap => GenerateOpenStreetMapHTML(),
                    MapProvider.GoogleMaps => GenerateGoogleMapsHTML(),
                    MapProvider.BingMaps => GenerateBingMapsHTML(),
                    MapProvider.MapBox => GenerateMapBoxHTML(),
                    _ => GenerateOpenStreetMapHTML()
                };

                if (_webView != null)
                {
                    _webView.NavigateToString(htmlContent);
                    System.Windows.MessageBox.Show("NavigateToString called");
                    
                    // Wait for navigation to complete
                    var tcs = new TaskCompletionSource<bool>();
                    EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
                    handler = (s, e) => {
                        _webView.NavigationCompleted -= handler;
                        tcs.SetResult(true);
                    };
                    _webView.NavigationCompleted += handler;
                    await tcs.Task;
                    
                    // Give time for scripts to execute
                    await Task.Delay(500);
                    
                    // Wait for the page to signal it's ready
                    while (!_isReady)
                    {
                        await Task.Delay(100);
                    }
                }

                _logger.LogInformation($"Real mapping service initialized with {provider}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to initialize mapping service with {provider}");
                return false;
            }
        }

        public async Task SetMapViewAsync(double latitude, double longitude, int zoomLevel)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;

                _currentLatitude = latitude;
                _currentLongitude = longitude;
                _currentZoom = zoomLevel;

                var script = $"setMapView({latitude}, {longitude}, {zoomLevel});";
                await _webView.CoreWebView2.ExecuteScriptAsync(script);

                _logger.LogInformation($"Map view set to {latitude}, {longitude} zoom {zoomLevel}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting map view");
            }
        }

        public async Task AddDispersionPlumeAsync(DispersionResult result, DispersionVisualizationOptions options)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;

                await ClearDispersionOverlaysAsync();

                var contourData = GenerateContourData(result, options);
                var overlayId = Guid.NewGuid().ToString();

                var script = $"addDispersionPlume('{overlayId}', {JsonSerializer.Serialize(contourData)});";
                await _webView.CoreWebView2.ExecuteScriptAsync(script);

                _dispersionOverlays.Add(overlayId);

                _logger.LogInformation($"Added dispersion plume overlay: {overlayId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding dispersion plume");
            }
        }

        public async Task AddDispersionGridAsync(IEnumerable<DispersionResult> gridResults, DispersionVisualizationOptions options)
        {
            try
            {
                if (_webView?.CoreWebView2 == null)
                {
                    _logger.LogError("WebView2 CoreWebView2 is null - cannot add dispersion grid");
                    return;
                }

                await ClearDispersionOverlaysAsync();

                var gridResultsList = gridResults.ToList();
                _logger.LogInformation($"Adding dispersion grid with {gridResultsList.Count} results");

                var gridData = gridResultsList.Select(r => new
                {
                    latitude = r.Latitude,
                    longitude = r.Longitude,
                    concentration = r.Concentration
                }).ToArray();

                _logger.LogInformation($"Grid data sample: First point at {gridData.FirstOrDefault()?.latitude}, {gridData.FirstOrDefault()?.longitude} with concentration {gridData.FirstOrDefault()?.concentration}");

                var contourData = new
                {
                    gridData = gridData,
                    contourLevels = options.ConcentrationLevels.ToArray(),
                    colors = options.ContourColors.ToArray(),
                    opacity = options.Opacity,
                    unit = "mg/m³"
                };

                var overlayId = Guid.NewGuid().ToString();
                var jsonData = JsonSerializer.Serialize(contourData);
                _logger.LogInformation($"Sending to JavaScript: addDispersionPlume('{overlayId}', {jsonData.Substring(0, Math.Min(200, jsonData.Length))}...)");

                var script = $"addDispersionPlume('{overlayId}', {jsonData});";
                await _webView.CoreWebView2.ExecuteScriptAsync(script);

                _dispersionOverlays.Add(overlayId);

                _logger.LogInformation($"Added dispersion grid overlay with {gridData.Length} points: {overlayId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding dispersion grid");
            }
        }

        public async Task ClearDispersionOverlaysAsync()
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;

                foreach (var overlayId in _dispersionOverlays)
                {
                    var script = $"removeDispersionPlume('{overlayId}');";
                    await _webView.CoreWebView2.ExecuteScriptAsync(script);
                }

                _dispersionOverlays.Clear();
                _logger.LogInformation("Cleared all dispersion overlays");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing dispersion overlays");
            }
        }

        public async Task ClearReleaseMarkersAsync()
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;

                var script = "clearReleaseMarkers();";
                await _webView.CoreWebView2.ExecuteScriptAsync(script);

                _logger.LogInformation("Cleared all release markers");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing release markers");
            }
        }

        public async Task AddWeatherStationAsync(WeatherStation station)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;

                var markerData = new
                {
                    id = station.Id,
                    lat = station.Latitude,
                    lng = station.Longitude,
                    title = station.Name,
                    type = "weather",
                    data = station.CurrentWeather,
                    isOnline = station.IsOnline
                };

                var script = $"addMarker({JsonSerializer.Serialize(markerData)});";
                await _webView.CoreWebView2.ExecuteScriptAsync(script);

                _logger.LogInformation($"Added weather station marker: {station.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding weather station");
            }
        }

        public async Task AddGasSensorAsync(GasSensor sensor)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;

                var markerData = new
                {
                    id = sensor.Id,
                    lat = sensor.Latitude,
                    lng = sensor.Longitude,
                    title = sensor.Name,
                    type = "gas_sensor",
                    data = sensor.LatestReading,
                    isOnline = sensor.IsOnline,
                    chemicals = sensor.DetectedChemicals
                };

                var script = $"addMarker({JsonSerializer.Serialize(markerData)});";
                await _webView.CoreWebView2.ExecuteScriptAsync(script);

                _logger.LogInformation($"Added gas sensor marker: {sensor.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding gas sensor");
            }
        }

        public async Task AddReleaseSourceAsync(Release release)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;

                var markerData = new
                {
                    id = release.Id.ToString(),
                    lat = release.Latitude,
                    lng = release.Longitude,
                    title = $"Release: {release.Chemical?.Name}",
                    type = "release",
                    data = release
                };

                var script = $"addMarker({JsonSerializer.Serialize(markerData)});";
                await _webView.CoreWebView2.ExecuteScriptAsync(script);

                _logger.LogInformation($"Added release source marker: {release.Chemical?.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding release source");
            }
        }

        public async Task AddReceptorAsync(Receptor receptor)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;

                var markerData = new
                {
                    id = receptor.Id.ToString(),
                    lat = receptor.Latitude,
                    lng = receptor.Longitude,
                    title = receptor.Name,
                    type = "receptor",
                    data = receptor
                };

                var script = $"addMarker({JsonSerializer.Serialize(markerData)});";
                await _webView.CoreWebView2.ExecuteScriptAsync(script);

                _logger.LogInformation($"Added receptor marker: {receptor.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding receptor");
            }
        }

        public async Task<TerrainData> LoadTerrainDataAsync(double minLat, double minLon, double maxLat, double maxLon)
        {
            try
            {
                _logger.LogInformation($"Loading terrain data for bounds: {minLat},{minLon} to {maxLat},{maxLon}");

                var terrainData = new TerrainData
                {
                    Latitude = (minLat + maxLat) / 2,
                    Longitude = (minLon + maxLon) / 2,
                    Elevation = 100,
                    LandUseType = "Urban",
                    SurfaceRoughness = 0.3,
                    DataSource = "USGS DEM"
                };

                await Task.Delay(100);
                return terrainData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading terrain data");
                throw;
            }
        }

        public async Task LoadBuildingDataAsync(double minLat, double minLon, double maxLat, double maxLon)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;

                _logger.LogInformation($"Loading building data for bounds: {minLat},{minLon} to {maxLat},{maxLon}");

                var script = $"loadBuildingData({minLat}, {minLon}, {maxLat}, {maxLon});";
                await _webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading building data");
            }
        }

        public async Task<(double latitude, double longitude)> ScreenToGeographicAsync(double x, double y)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return (0, 0);

                var script = $"screenToGeographic({x}, {y});";
                var result = await _webView.CoreWebView2.ExecuteScriptAsync(script);

                var coords = JsonSerializer.Deserialize<double[]>(result);
                return (coords?[0] ?? 0, coords?[1] ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error converting screen to geographic coordinates");
                return (0, 0);
            }
        }

        public async Task<(double x, double y)> GeographicToScreenAsync(double latitude, double longitude)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return (0, 0);

                var script = $"geographicToScreen({latitude}, {longitude});";
                var result = await _webView.CoreWebView2.ExecuteScriptAsync(script);

                var coords = JsonSerializer.Deserialize<double[]>(result);
                return (coords?[0] ?? 0, coords?[1] ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error converting geographic to screen coordinates");
                return (0, 0);
            }
        }

        public async Task AddDownwindCorridorAsync(double latitude, double longitude, double windDirectionDeg, double windSpeed, double temperature, string stabilityClass)
        {
            Console.WriteLine($"=== AddDownwindCorridorAsync called with lat={latitude}, lng={longitude}, windDir={windDirectionDeg}, windSpeed={windSpeed} ===");
            if (_webView?.CoreWebView2 == null) return;

            int numRings = 8;
            double maxDistance = 2000 + windSpeed * 200;
            double fanAngle = 60.0;
            double ringSpacing = maxDistance / numRings;
            double windDirectionRad = windDirectionDeg * Math.PI / 180.0;

            switch (stabilityClass.ToUpperInvariant())
            {
                case "A": fanAngle = 90; break;
                case "B": fanAngle = 80; break;
                case "C": fanAngle = 70; break;
                case "D": fanAngle = 60; break;
                case "E": fanAngle = 50; break;
                case "F": fanAngle = 40; break;
            }

            double halfFanRad = (fanAngle / 2.0) * Math.PI / 180.0;

            var rings = new List<List<double[]>>();
            for (int r = 1; r <= numRings; r++)
            {
                double radius = r * ringSpacing;
                int numPoints = 40;
                var ring = new List<double[]>();
                for (int i = 0; i <= numPoints; i++)
                {
                    double theta = windDirectionRad - halfFanRad + (i * fanAngle * Math.PI / 180.0) / numPoints;
                    double lat = latitude + (radius * Math.Cos(theta)) / 111000.0;
                    double lon = longitude + (radius * Math.Sin(theta)) / (111000.0 * Math.Cos(latitude * Math.PI / 180.0));
                    ring.Add(new[] { lat, lon });
                }
                rings.Add(ring);
            }

            var edge1 = new List<double[]>();
            var edge2 = new List<double[]>();
            for (int r = 0; r <= numRings; r++)
            {
                double radius = r * ringSpacing;
                double theta1 = windDirectionRad - halfFanRad;
                double theta2 = windDirectionRad + halfFanRad;
                double lat1 = latitude + (radius * Math.Cos(theta1)) / 111000.0;
                double lon1 = longitude + (radius * Math.Sin(theta1)) / (111000.0 * Math.Cos(latitude * Math.PI / 180.0));
                double lat2 = latitude + (radius * Math.Cos(theta2)) / 111000.0;
                double lon2 = longitude + (radius * Math.Sin(theta2)) / (111000.0 * Math.Cos(latitude * Math.PI / 180.0));
                edge1.Add(new[] { lat1, lon1 });
                edge2.Add(new[] { lat2, lon2 });
            }

            Console.WriteLine($"Generated {rings.Count} rings");
            if (rings.Count > 0) Console.WriteLine($"First ring has {rings[0].Count} points");
            Console.WriteLine($"Edge1 has {edge1.Count} points, Edge2 has {edge2.Count} points");

            var corridorData = new {
                origin = new[] { latitude, longitude },
                rings = rings,
                edge1 = edge1,
                edge2 = edge2,
                color = "#2196F3",
                opacity = 0.7
            };
            var overlayId = Guid.NewGuid().ToString();
            var json = JsonSerializer.Serialize(corridorData);
            Console.WriteLine($"[DEBUG] Sending corridor JS: addDownwindCorridor('{overlayId}', {json.Substring(0, Math.Min(500, json.Length))}...)");
            var script = $"console.log('[JS DEBUG] addDownwindCorridor called with id: {overlayId}'); console.log('addDownwindCorridor is', typeof addDownwindCorridor); try {{ addDownwindCorridor('{overlayId}', {json}); console.log('addDownwindCorridor executed successfully'); }} catch (e) {{ console.error('Error calling addDownwindCorridor:', e); }}";
            await _webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _logger.LogInformation($"Map navigation completed successfully: {e.IsSuccess}");
            System.Windows.MessageBox.Show($"Navigation completed: {e.IsSuccess}, Source: {_webView?.Source?.ToString() ?? "null"}");
        }

        private void OnCoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            Console.WriteLine("=== OnCoreWebView2InitializationCompleted called ===");
            System.Diagnostics.Debug.WriteLine("=== OnCoreWebView2InitializationCompleted called ===");

            if (e.IsSuccess && _webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                Console.WriteLine("=== C# WebMessageReceived handler attached! ===");
                System.Diagnostics.Debug.WriteLine("=== C# WebMessageReceived handler attached! ===");
                System.Windows.MessageBox.Show("WebView2 initialized and WebMessageReceived handler attached!", "Initialization Success", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                _logger.LogInformation("WebView2 initialization completed successfully");
                // Automatically open DevTools for debugging
                _webView.CoreWebView2.OpenDevToolsWindow();
            }
            else
            {
                Console.WriteLine($"=== WebView2 initialization failed: {e.InitializationException?.Message} ===");
                System.Diagnostics.Debug.WriteLine($"=== WebView2 initialization failed: {e.InitializationException?.Message} ===");
                System.Windows.MessageBox.Show($"WebView2 initialization failed: {e.InitializationException?.Message}", "Initialization Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                _logger.LogError($"WebView2 initialization failed: {e.InitializationException?.Message}");
            }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Console.WriteLine("=== C# WebMessage Received! ===");
            System.Diagnostics.Debug.WriteLine("=== C# WebMessage Received! ===");
            try
            {
                string message;
                try
                {
                    message = e.TryGetWebMessageAsString();
                }
                catch (Exception ex1)
                {
                    Console.WriteLine($"TryGetWebMessageAsString failed: {ex1.Message}");
                    try
                    {
                        message = e.WebMessageAsJson;
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine($"WebMessageAsJson failed: {ex2.Message}");
                        System.Windows.MessageBox.Show($"Failed to get message content!\nTryGetWebMessageAsString: {ex1.Message}\nWebMessageAsJson: {ex2.Message}", "Message Retrieval Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                        return;
                    }
                }

                Console.WriteLine($"Message: {message}");
                System.Diagnostics.Debug.WriteLine($"Message: {message}");

                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;

                if (root.TryGetProperty("type", out var typeElement))
                {
                    var messageType = typeElement.GetString();
                    Console.WriteLine($"Message type: {messageType}");
                    System.Diagnostics.Debug.WriteLine($"Message type: {messageType}");

                    switch (messageType)
                    {
                        case "mapClick":
                            Console.WriteLine("Calling HandleMapClick");
                            HandleMapClick(root);
                            break;
                        case "mapViewChanged":
                            HandleMapViewChanged(root);
                            break;
                        case "ready":
                            Console.WriteLine("=== Page ready message received! ===");
                            _isReady = true;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
                _logger.LogError(ex, "Error processing web message");
                System.Windows.MessageBox.Show($"WebMessage Error!\nError: {ex.Message}\nStack: {ex.StackTrace}", "Debug Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void HandleMapClick(JsonElement data)
        {
            if (data.TryGetProperty("latitude", out var latElement) && data.TryGetProperty("longitude", out var lngElement))
            {
                var latitude = latElement.GetDouble();
                var longitude = lngElement.GetDouble();
                var args = new MapClickEventArgs
                {
                    Latitude = latitude,
                    Longitude = longitude,
                    ScreenX = data.TryGetProperty("screenX", out var xElement) ? xElement.GetDouble() : 0,
                    ScreenY = data.TryGetProperty("screenY", out var yElement) ? yElement.GetDouble() : 0,
                    IsRightClick = data.TryGetProperty("rightClick", out var rightClickElement) && rightClickElement.GetBoolean()
                };
                Console.WriteLine($"=== Triggering MapClicked event for coordinates: {args.Latitude:F6}, {args.Longitude:F6} ===");
                Console.WriteLine($"Release marker should already be added by JavaScript");
                MapClicked?.Invoke(this, args);
                Console.WriteLine($"Map Click Processed!\nLat: {args.Latitude:F6}\nLng: {args.Longitude:F6}\nEvent triggered!");
            }
            else
            {
                Console.WriteLine("ERROR: Could not find latitude/longitude properties in map click data");
                _logger.LogError("Map click data missing required latitude/longitude properties");
            }
        }

        private void HandleMapViewChanged(JsonElement data)
        {
            if (data.TryGetProperty("centerLat", out var centerLatElement) && data.TryGetProperty("centerLng", out var centerLngElement) && data.TryGetProperty("zoom", out var zoomElement))
            {
                var args = new MapViewChangedEventArgs
                {
                    CenterLatitude = centerLatElement.GetDouble(),
                    CenterLongitude = centerLngElement.GetDouble(),
                    ZoomLevel = zoomElement.GetInt32(),
                    MinLatitude = data.TryGetProperty("minLat", out var minLatElement) ? minLatElement.GetDouble() : 0,
                    MinLongitude = data.TryGetProperty("minLng", out var minLngElement) ? minLngElement.GetDouble() : 0,
                    MaxLatitude = data.TryGetProperty("maxLat", out var maxLatElement) ? maxLatElement.GetDouble() : 0,
                    MaxLongitude = data.TryGetProperty("maxLng", out var maxLngElement) ? maxLngElement.GetDouble() : 0
                };
                MapViewChanged?.Invoke(this, args);
            }
        }

        private string GenerateOpenStreetMapHTML()
        {
            return @"<!DOCTYPE html>
<html>
<head>
    <title>Chemical Dispersion Map</title>
</head>
<body>
    <div id='map' style='height: 100%; width: 100%; background: lightblue;'>Map Placeholder</div>
    <script>
        console.log('Map initialized with plume visualization');
        function addDownwindCorridor(id, data) {
            console.log('[JS DEBUG] addDownwindCorridor called with id:', id);
            console.log('addDownwindCorridor is defined');
            // Placeholder for corridor drawing
        }
        // Send test message to C#
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'test', message: 'Map loaded successfully' });
            window.chrome.webview.postMessage({ type: 'ready' });
        }
    </script>
</body>
</html>";
        }

        private string GenerateGoogleMapsHTML()
        {
            // ...existing code for HTML generation...
            return "";
        }

        private string GenerateBingMapsHTML()
        {
            // ...existing code for HTML generation...
            return "";
        }

        private string GenerateMapBoxHTML()
        {
            // ...existing code for HTML generation...
            return "";
        }

        private object GenerateContourData(DispersionResult result, DispersionVisualizationOptions options)
        {
            var coordinates = new List<List<double[]>>();
            var centerLat = result.Latitude;
            var centerLng = result.Longitude;
            var contour = new List<double[]>
            {
                new[] { centerLat + 0.01, centerLng - 0.01 },
                new[] { centerLat + 0.01, centerLng + 0.01 },
                new[] { centerLat - 0.01, centerLng + 0.01 },
                new[] { centerLat - 0.01, centerLng - 0.01 },
                new[] { centerLat + 0.01, centerLng - 0.01 }
            };
            coordinates.Add(contour);
            return new
            {
                coordinates = coordinates,
                color = options.ContourColors.FirstOrDefault() ?? "#FF0000",
                fillColor = options.ContourColors.FirstOrDefault() ?? "#FF0000",
                opacity = options.Opacity
            };
        }
    }
}
