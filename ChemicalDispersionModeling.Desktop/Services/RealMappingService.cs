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

namespace ChemicalDispersionModeling.Desktop.Services;

public partial class RealMappingService(ILogger<RealMappingService> logger) : IRealMappingService
{
    private WebView2? _webView;
    private double _currentLatitude;
    private double _currentLongitude;
    private int _currentZoom = 10;
    private readonly List<string> _dispersionOverlays = [];
    private bool _isReady;

    public event EventHandler<MapClickEventArgs>? MapClicked;
    public event EventHandler<MapViewChangedEventArgs>? MapViewChanged;

    public void SetWebView(WebView2 webView)
    {
        _webView = webView;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.CoreWebView2InitializationCompleted += OnCoreWebView2InitializationCompleted;

        if (_webView.CoreWebView2 != null)
        {
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        }
    }

    public async Task<bool> InitializeAsync(MapProvider provider, string apiKey = "")
    {
        try
        {
            logger.LogInformation("Initializing real mapping service with {Provider}", provider);

            if (_webView != null)
            {
                await _webView.EnsureCoreWebView2Async();
            }

            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
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

                var tcs = new TaskCompletionSource<bool>();

                void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    _webView.NavigationCompleted -= Handler;
                    tcs.SetResult(true);
                }

                _webView.NavigationCompleted += Handler;
                await tcs.Task;

                await Task.Delay(500);

                while (!_isReady)
                {
                    await Task.Delay(100);
                }
            }

            logger.LogInformation("Real mapping service initialized with {Provider}", provider);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize mapping service with {Provider}", provider);
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

            logger.LogInformation("Map view set to {Latitude}, {Longitude} zoom {ZoomLevel}", latitude, longitude, zoomLevel);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting map view");
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

            logger.LogInformation("Added dispersion plume overlay: {OverlayId}", overlayId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding dispersion plume");
        }
    }

    public async Task AddDispersionGridAsync(IEnumerable<DispersionResult> gridResults, DispersionVisualizationOptions options)
    {
        try
        {
            if (_webView?.CoreWebView2 == null)
            {
                logger.LogError("WebView2 CoreWebView2 is null - cannot add dispersion grid");
                return;
            }

            await ClearDispersionOverlaysAsync();

            var gridResultsList = gridResults.ToList();
            logger.LogInformation("Adding dispersion grid with {Count} results", gridResultsList.Count);

            var gridData = gridResultsList.Select(r => new
            {
                latitude = r.Latitude,
                longitude = r.Longitude,
                concentration = r.Concentration
            }).ToArray();

            logger.LogInformation("Grid data sample: First point at {Latitude}, {Longitude} with concentration {Concentration}", gridData.FirstOrDefault()?.latitude, gridData.FirstOrDefault()?.longitude, gridData.FirstOrDefault()?.concentration);

            var contourData = new
            {
                gridData,
                contourLevels = options.ConcentrationLevels.ToArray(),
                colors = options.ContourColors.ToArray(),
                opacity = options.Opacity,
                unit = "mg/m³"
            };

            var overlayId = Guid.NewGuid().ToString();
            var jsonData = JsonSerializer.Serialize(contourData);
            logger.LogInformation("Sending to JavaScript: addDispersionPlume('{OverlayId}', {JsonData}...)", overlayId, jsonData[..Math.Min(200, jsonData.Length)]);

            var script = $"addDispersionPlume('{overlayId}', {jsonData});";
            await _webView.CoreWebView2.ExecuteScriptAsync(script);

            _dispersionOverlays.Add(overlayId);

            logger.LogInformation("Added dispersion grid overlay with {PointCount} points: {OverlayId}", gridData.Length, overlayId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding dispersion grid");
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
            logger.LogInformation("Cleared all dispersion overlays");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error clearing dispersion overlays");
        }
    }

    public async Task ClearReleaseMarkersAsync()
    {
        try
        {
            if (_webView?.CoreWebView2 == null) return;

            var script = "clearReleaseMarkers();";
            await _webView.CoreWebView2.ExecuteScriptAsync(script);

            logger.LogInformation("Cleared all release markers");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error clearing release markers");
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

            logger.LogInformation("Added weather station marker: {StationName}", station.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding weather station");
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

            logger.LogInformation("Added gas sensor marker: {SensorName}", sensor.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding gas sensor");
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

            logger.LogInformation("Added release source marker: {ChemicalName}", release.Chemical?.Name ?? "Unknown");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding release source");
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

            logger.LogInformation("Added receptor marker: {ReceptorName}", receptor.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding receptor");
        }
    }

    public async Task<TerrainData> LoadTerrainDataAsync(double minLat, double minLon, double maxLat, double maxLon)
    {
        try
        {
            logger.LogInformation("Loading terrain data for bounds: {MinLat},{MinLon} to {MaxLat},{MaxLon}", minLat, minLon, maxLat, maxLon);

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
            logger.LogError(ex, "Error loading terrain data");
            throw;
        }
    }

    public async Task LoadBuildingDataAsync(double minLat, double minLon, double maxLat, double maxLon)
    {
        try
        {
            if (_webView?.CoreWebView2 == null) return;

            logger.LogInformation("Loading building data for bounds: {MinLat},{MinLon} to {MaxLat},{MaxLon}", minLat, minLon, maxLat, maxLon);

            var script = $"loadBuildingData({minLat}, {minLon}, {maxLat}, {maxLon});";
            await _webView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading building data");
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
            logger.LogError(ex, "Error converting screen to geographic coordinates");
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
            logger.LogError(ex, "Error converting geographic to screen coordinates");
            return (0, 0);
        }
    }

    public async Task AddDownwindCorridorAsync(double latitude, double longitude, double windDirectionDeg, double windSpeed, double temperature, string stabilityClass)
    {
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
                ring.Add([lat, lon]);
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
            edge1.Add([lat1, lon1]);
            edge2.Add([lat2, lon2]);
        }

        var corridorData = new
        {
            origin = new[] { latitude, longitude },
            rings,
            edge1,
            edge2,
            color = "#2196F3",
            opacity = 0.7
        };
        var overlayId = Guid.NewGuid().ToString();
        var json = JsonSerializer.Serialize(corridorData);
        var script = $"addDownwindCorridor('{overlayId}', {json});";
        await _webView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        logger.LogInformation("Map navigation completed successfully: {IsSuccess}", e.IsSuccess);
    }

    private void OnCoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (e.IsSuccess && _webView?.CoreWebView2 != null)
        {
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            logger.LogInformation("WebView2 initialization completed successfully");
            _webView.CoreWebView2.OpenDevToolsWindow();
        }
        else
        {
            logger.LogError("WebView2 initialization failed: {Message}", e.InitializationException?.Message ?? "Unknown error");
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string message = e.TryGetWebMessageAsString();

            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;

            if (root.TryGetProperty("type", out var typeElement))
            {
                var messageType = typeElement.GetString();

                switch (messageType)
                {
                    case "mapClick":
                        HandleMapClick(root);
                        break;
                    case "mapViewChanged":
                        HandleMapViewChanged(root);
                        break;
                    case "ready":
                        _isReady = true;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing web message");
        }
    }

    private void HandleMapClick(JsonElement data)
    {
        if (data.TryGetProperty("latitude", out var latElement) && data.TryGetProperty("longitude", out var lngElement))
        {
            var args = new MapClickEventArgs
            {
                Latitude = latElement.GetDouble(),
                Longitude = lngElement.GetDouble(),
                ScreenX = data.TryGetProperty("screenX", out var xElement) ? xElement.GetDouble() : 0,
                ScreenY = data.TryGetProperty("screenY", out var yElement) ? yElement.GetDouble() : 0,
                IsRightClick = data.TryGetProperty("rightClick", out var rightClickElement) && rightClickElement.GetBoolean()
            };
            MapClicked?.Invoke(this, args);
        }
        else
        {
            logger.LogError("Map click data missing required latitude/longitude properties");
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

    private static string GenerateOpenStreetMapHTML() =>
        """
        <!DOCTYPE html>
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
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'ready' });
                }
            </script>
        </body>
        </html>
        """;

    private static string GenerateGoogleMapsHTML() => "";

    private static string GenerateBingMapsHTML() => "";

    private static string GenerateMapBoxHTML() => "";

    private static object GenerateContourData(DispersionResult result, DispersionVisualizationOptions options)
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
            coordinates,
            color = options.ContourColors.FirstOrDefault() ?? "#FF0000",
            fillColor = options.ContourColors.FirstOrDefault() ?? "#FF0000",
            opacity = options.Opacity
        };
    }
}
