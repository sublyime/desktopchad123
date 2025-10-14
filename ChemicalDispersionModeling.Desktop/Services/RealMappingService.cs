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

public class RealMappingService : IRealMappingService
{
    private readonly ILogger<RealMappingService> _logger;
    private WebView2? _webView;
    private double _currentLatitude;
    private double _currentLongitude;
    private int _currentZoom = 10;
    private readonly List<string> _dispersionOverlays = new();
    private bool _isReady;

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

        if (_webView.CoreWebView2 != null)
        {
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        }
    }

    public async Task<bool> InitializeAsync(MapProvider provider, string apiKey = "")
    {
        try
        {
            _logger.LogInformation("Initializing real mapping service with {Provider}", provider);

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

            _logger.LogInformation("Real mapping service initialized with {Provider}", provider);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize mapping service with {Provider}", provider);
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

            _logger.LogInformation("Map view set to {Latitude}, {Longitude} zoom {ZoomLevel}", latitude, longitude, zoomLevel);
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
            if (_webView?.CoreWebView2 == null)
            {
                _logger.LogError("WebView2 CoreWebView2 is null - cannot add dispersion plume");
                return;
            }

            await ClearDispersionOverlaysAsync();

            var contourData = GenerateContourData(result, options);
            var overlayId = Guid.NewGuid().ToString();

            var jsonData = JsonSerializer.Serialize(contourData);
            _logger.LogInformation("Adding single dispersion plume at {Lat}, {Lon}", result.Latitude, result.Longitude);
            _logger.LogInformation("Contour data: {Data}", jsonData.Substring(0, Math.Min(200, jsonData.Length)));

            var script = $"if(typeof addDispersionPlume === 'function') {{ addDispersionPlume('{overlayId}', {jsonData}); }} else {{ console.error('addDispersionPlume not defined'); }}";
            await _webView.CoreWebView2.ExecuteScriptAsync(script);

            _dispersionOverlays.Add(overlayId);

            _logger.LogInformation("Added dispersion plume overlay: {OverlayId}", overlayId);
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
            _logger.LogInformation("Adding dispersion grid with {Count} results", gridResultsList.Count);

            var gridData = gridResultsList.Select(r => new
            {
                latitude = r.Latitude,
                longitude = r.Longitude,
                concentration = r.Concentration
            }).ToArray();

            _logger.LogInformation("Grid data sample: First point at {Latitude}, {Longitude} with concentration {Concentration}", 
                gridData.FirstOrDefault()?.latitude, 
                gridData.FirstOrDefault()?.longitude, 
                gridData.FirstOrDefault()?.concentration);

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
            _logger.LogInformation("Sending to JavaScript: addDispersionPlume('{OverlayId}', {JsonData}...)", 
                overlayId, 
                jsonData[..Math.Min(200, jsonData.Length)]);

            var script = $"addDispersionPlume('{overlayId}', {jsonData});";
            await _webView.CoreWebView2.ExecuteScriptAsync(script);

            _dispersionOverlays.Add(overlayId);

            _logger.LogInformation("Added dispersion grid overlay with {PointCount} points: {OverlayId}", gridData.Length, overlayId);
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

            _logger.LogInformation("Added weather station marker: {StationName}", station.Name);
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

            _logger.LogInformation("Added gas sensor marker: {SensorName}", sensor.Name);
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

            _logger.LogInformation("Added release source marker: {ChemicalName}", release.Chemical?.Name ?? "Unknown");
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

            _logger.LogInformation("Added receptor marker: {ReceptorName}", receptor.Name);
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
            _logger.LogInformation("Loading terrain data for bounds: {MinLat},{MinLon} to {MaxLat},{MaxLon}", 
                minLat, minLon, maxLat, maxLon);

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

            _logger.LogInformation("Loading building data for bounds: {MinLat},{MinLon} to {MaxLat},{MaxLon}", 
                minLat, minLon, maxLat, maxLon);

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

    public async Task AddDownwindCorridorAsync(double latitude, double longitude, double windDirectionDeg, 
        double windSpeed, double temperature, string stabilityClass)
    {
        try
        {
            if (_webView?.CoreWebView2 == null)
            {
                _logger.LogError("WebView2 CoreWebView2 is null - cannot add downwind corridor");
                return;
            }

            int numRings = 8;
            double maxDistance = 2000 + windSpeed * 200;
            double fanAngle = 60.0;
            double ringSpacing = maxDistance / numRings;
            double windDirectionRad = windDirectionDeg * Math.PI / 180.0;

            _logger.LogInformation("Creating corridor: windDir={WindDir}°, windSpeed={WindSpeed}m/s, maxDist={MaxDist}m", 
                windDirectionDeg, windSpeed, maxDistance);

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
            
            _logger.LogInformation("Corridor has {RingCount} rings, first ring has {PointCount} points", 
                rings.Count, rings.FirstOrDefault()?.Count ?? 0);
            _logger.LogInformation("Corridor JSON length: {Length}", json.Length);
            
            var script = $"if(typeof addDownwindCorridor === 'function') {{ addDownwindCorridor('{overlayId}', {json}); }} else {{ console.error('addDownwindCorridor not defined'); }}";
            await _webView.CoreWebView2.ExecuteScriptAsync(script);
            
            _dispersionOverlays.Add(overlayId);
            
            _logger.LogInformation("Downwind corridor {OverlayId} command sent to JavaScript", overlayId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding downwind corridor");
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _logger.LogInformation("Map navigation completed successfully: {IsSuccess}", e.IsSuccess);
    }

    private void OnCoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (e.IsSuccess && _webView?.CoreWebView2 != null)
        {
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _logger.LogInformation("WebView2 initialization completed successfully");
            _webView.CoreWebView2.OpenDevToolsWindow();
        }
        else
        {
            _logger.LogError("WebView2 initialization failed: {Message}", e.InitializationException?.Message ?? "Unknown error");
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
            _logger.LogError(ex, "Error processing web message");
        }
    }

    private void HandleMapClick(JsonElement data)
    {
        if (data.TryGetProperty("latitude", out var latElement) && 
            data.TryGetProperty("longitude", out var lngElement))
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
            _logger.LogError("Map click data missing required latitude/longitude properties");
        }
    }

    private void HandleMapViewChanged(JsonElement data)
    {
        if (data.TryGetProperty("centerLat", out var centerLatElement) && 
            data.TryGetProperty("centerLng", out var centerLngElement) && 
            data.TryGetProperty("zoom", out var zoomElement))
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

    private static string GenerateOpenStreetMapHTML()
    {
        return """
<!DOCTYPE html>
<html>
<head>
    <title>Chemical Dispersion Map</title>
    <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" />
    <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
    <style>
        body, html { margin: 0; padding: 0; height: 100%; width: 100%; }
        #map { height: 100%; width: 100%; }
        #debug { position: absolute; top: 10px; right: 10px; background: white; padding: 10px; z-index: 1000; max-width: 300px; max-height: 200px; overflow: auto; font-size: 12px; }
    </style>
</head>
<body>
    <div id='map'></div>
    <div id='debug'>Map Ready</div>
    <script>
        function debugLog(msg) {
            console.log(msg);
            const debugDiv = document.getElementById('debug');
            debugDiv.innerHTML = msg + '<br>' + debugDiv.innerHTML;
        }
        
        let map = L.map('map').setView([29.5, -95.2], 10);
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            attribution: '© OpenStreetMap contributors'
        }).addTo(map);
        
        let markers = {};
        let overlays = {};
        let layerGroups = {};
        
        debugLog('Map initialized');
        
        function setMapView(lat, lng, zoom) {
            debugLog(`setMapView: ${lat}, ${lng}, zoom ${zoom}`);
            map.setView([lat, lng], zoom);
        }
        
        function addMarker(data) {
            debugLog(`addMarker: ${data.id} at ${data.lat}, ${data.lng}`);
            if (markers[data.id]) {
                map.removeLayer(markers[data.id]);
            }
            let marker = L.marker([data.lat, data.lng]).addTo(map);
            marker.bindPopup(data.title);
            markers[data.id] = marker;
        }
        
        function addDispersionPlume(id, data) {
            debugLog(`addDispersionPlume called with id: ${id}`);
            debugLog(`Data keys: ${Object.keys(data).join(', ')}`);
            
            // Remove existing overlay
            if (layerGroups[id]) {
                map.removeLayer(layerGroups[id]);
                delete layerGroups[id];
            }
            
            // Create layer group for all plume elements
            let layerGroup = L.layerGroup();
            
            // Handle grid data (multiple points)
            if (data.gridData && Array.isArray(data.gridData)) {
                debugLog(`Processing ${data.gridData.length} grid points`);
                
                let count = 0;
                data.gridData.forEach(point => {
                    if (point.latitude && point.longitude && point.concentration > 0) {
                        // Calculate radius and opacity based on concentration
                        let radius = 100; // Base radius in meters
                        let opacity = Math.min(Math.max(point.concentration / 10, 0.1), 0.9);
                        
                        let circle = L.circle([point.latitude, point.longitude], {
                            radius: radius,
                            color: '#ff0000',
                            fillColor: '#ff0000',
                            fillOpacity: opacity,
                            weight: 0
                        });
                        
                        circle.addTo(layerGroup);
                        count++;
                    }
                });
                
                debugLog(`Added ${count} circles to plume`);
            }
            
            // Add layer group to map
            layerGroup.addTo(map);
            layerGroups[id] = layerGroup;
            
            debugLog(`Plume overlay ${id} added to map`);
        }
        
        function removeDispersionPlume(id) {
            debugLog(`removeDispersionPlume: ${id}`);
            if (layerGroups[id]) {
                map.removeLayer(layerGroups[id]);
                delete layerGroups[id];
            }
        }
        
        function clearReleaseMarkers() {
            debugLog('clearReleaseMarkers called');
            Object.values(markers).forEach(marker => map.removeLayer(marker));
            markers = {};
        }
        
        function addDownwindCorridor(id, data) {
            debugLog(`addDownwindCorridor called with id: ${id}`);
            debugLog(`Data keys: ${Object.keys(data).join(', ')}`);
            debugLog(`Origin: ${data.origin ? data.origin.join(', ') : 'none'}`);
            debugLog(`Rings count: ${data.rings ? data.rings.length : 0}`);
            
            // Remove existing corridor
            if (layerGroups[id]) {
                map.removeLayer(layerGroups[id]);
                delete layerGroups[id];
            }
            
            // Create layer group for all corridor elements
            let layerGroup = L.layerGroup();
            
            // Add origin marker
            if (data.origin && data.origin.length === 2) {
                let originMarker = L.circleMarker(data.origin, {
                    radius: 8,
                    color: '#000',
                    fillColor: data.color || '#2196F3',
                    fillOpacity: 1,
                    weight: 2
                });
                originMarker.addTo(layerGroup);
                debugLog(`Added origin marker at ${data.origin.join(', ')}`);
            }
            
            // Add rings
            if (data.rings && Array.isArray(data.rings)) {
                data.rings.forEach((ring, index) => {
                    if (ring && ring.length > 0) {
                        let poly = L.polygon(ring, {
                            color: data.color || '#2196F3',
                            fillColor: data.color || '#2196F3',
                            fillOpacity: (data.opacity || 0.7) * 0.2,
                            weight: 2,
                            opacity: 0.8
                        });
                        poly.addTo(layerGroup);
                        debugLog(`Added ring ${index + 1} with ${ring.length} points`);
                    }
                });
            }
            
            // Add edges
            if (data.edge1 && Array.isArray(data.edge1)) {
                let edge1Line = L.polyline(data.edge1, {
                    color: data.color || '#2196F3',
                    weight: 2,
                    opacity: 0.8,
                    dashArray: '5, 10'
                });
                edge1Line.addTo(layerGroup);
                debugLog(`Added edge1 with ${data.edge1.length} points`);
            }
            
            if (data.edge2 && Array.isArray(data.edge2)) {
                let edge2Line = L.polyline(data.edge2, {
                    color: data.color || '#2196F3',
                    weight: 2,
                    opacity: 0.8,
                    dashArray: '5, 10'
                });
                edge2Line.addTo(layerGroup);
                debugLog(`Added edge2 with ${data.edge2.length} points`);
            }
            
            // Add layer group to map
            layerGroup.addTo(map);
            layerGroups[id] = layerGroup;
            
            // Fit map to corridor bounds if possible
            try {
                let bounds = layerGroup.getBounds();
                if (bounds.isValid()) {
                    map.fitBounds(bounds, { padding: [50, 50] });
                    debugLog('Map fitted to corridor bounds');
                }
            } catch (e) {
                debugLog('Could not fit bounds: ' + e.message);
            }
            
            debugLog(`Corridor ${id} added with ${layerGroup.getLayers().length} layers`);
        }
        
        function loadBuildingData(minLat, minLon, maxLat, maxLon) {
            debugLog(`loadBuildingData: ${minLat},${minLon} to ${maxLat},${maxLon}`);
        }
        
        function screenToGeographic(x, y) {
            let point = map.containerPointToLatLng([x, y]);
            return [point.lat, point.lng];
        }
        
        function geographicToScreen(lat, lng) {
            let point = map.latLngToContainerPoint([lat, lng]);
            return [point.x, point.y];
        }
        
        map.on('click', function(e) {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({
                    type: 'mapClick',
                    latitude: e.latlng.lat,
                    longitude: e.latlng.lng,
                    screenX: e.containerPoint.x,
                    screenY: e.containerPoint.y
                });
            }
        });
        
        map.on('moveend zoomend', function() {
            let center = map.getCenter();
            let bounds = map.getBounds();
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({
                    type: 'mapViewChanged',
                    centerLat: center.lat,
                    centerLng: center.lng,
                    zoom: map.getZoom(),
                    minLat: bounds.getSouth(),
                    minLng: bounds.getWest(),
                    maxLat: bounds.getNorth(),
                    maxLng: bounds.getEast()
                });
            }
        });
        
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'ready' });
        }
        
        debugLog('All functions registered');
    </script>
</body>
</html>
""";
    }

    private static string GenerateGoogleMapsHTML()
    {
        return """
<!DOCTYPE html>
<html>
<head>
    <title>Chemical Dispersion Map</title>
    <style>
        body, html { margin: 0; padding: 0; height: 100%; width: 100%; }
        #map { height: 100%; width: 100%; }
    </style>
</head>
<body>
    <div id='map'></div>
    <script>
        console.log('Google Maps placeholder - API key required');
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'ready' });
        }
    </script>
</body>
</html>
""";
    }

    private static string GenerateBingMapsHTML()
    {
        return """
<!DOCTYPE html>
<html>
<head>
    <title>Chemical Dispersion Map</title>
    <style>
        body, html { margin: 0; padding: 0; height: 100%; width: 100%; }
        #map { height: 100%; width: 100%; }
    </style>
</head>
<body>
    <div id='map'></div>
    <script>
        console.log('Bing Maps placeholder - API key required');
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'ready' });
        }
    </script>
</body>
</html>
""";
    }

    private static string GenerateMapBoxHTML()
    {
        return """
<!DOCTYPE html>
<html>
<head>
    <title>Chemical Dispersion Map</title>
    <style>
        body, html { margin: 0; padding: 0; height: 100%; width: 100%; }
        #map { height: 100%; width: 100%; }
    </style>
</head>
<body>
    <div id='map'></div>
    <script>
        console.log('MapBox placeholder - API key required');
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ type: 'ready' });
        }
    </script>
</body>
</html>
""";
    }

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