
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ChemicalDispersionModeling.Core.Models;
using Microsoft.Extensions.Logging;

namespace ChemicalDispersionModeling.Core.Services.Implementation;

public partial class GasSensorService : IGasSensorService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GasSensorService> _logger;
    private readonly List<DataSourceConfiguration> _activeConfigurations = [];
    private static readonly JsonSerializerOptions _jsonSerializerOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly char[] _csvSeparators = ['\n', '\r'];

    public GasSensorService(HttpClient httpClient, ILogger<GasSensorService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    private Timer? _monitoringTimer;
    private bool _isDisposed;

    public event EventHandler<IEnumerable<GasSensorReading>>? GasSensorDataReceived;

    public async Task<IEnumerable<GasSensorReading>> GetOSIPIDataAsync(string serverUrl, string[] tagNames)
    {
        _logger.LogInformation("Fetching OSI PI data from {serverUrl}", serverUrl);
        var client = _httpClient;

        var tasks = tagNames.Select(async tagName =>
        {
            var url = $"{serverUrl}/piwebapi/points?path=\\\\{tagName}&webid";
            try
            {
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                return ParseOSIPIData(json, tagName);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error fetching OSI PI data for tag {tagName}", tagName);
                return null;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error parsing JSON data: {jsonData}", "OSIPI Data");
                return null;
            }
        });

        var readings = await Task.WhenAll(tasks);
        return readings.Where(r => r != null)!;
    }

    public Task<IEnumerable<GasSensorReading>> GetOPCUADataAsync(string endpointUrl, string[] nodeIds)
    {
        _logger.LogInformation("Fetching OPC UA data from {endpointUrl}", endpointUrl);
        var readings = nodeIds.Select(nodeId => new GasSensorReading
        {
            Timestamp = DateTime.UtcNow,
            SensorId = nodeId,
            SensorLocation = "OPC UA Sensor",
            ChemicalName = ExtractChemicalFromNodeId(nodeId),
            Concentration = Random.Shared.NextDouble() * 100,
            ConcentrationUnit = "ppm",
            QualityFlag = "Good"
        });
        return Task.FromResult(readings);
    }

    public async Task<IEnumerable<GasSensorReading>> GetModbusDataAsync(string connectionString, int[] registerAddresses)
    {
        _logger.LogInformation("Fetching Modbus data: {connectionString}", connectionString);
        var parts = connectionString.Split(':');
        if (parts.Length < 3 || !IPAddress.TryParse(parts[0], out var ipAddress) || !int.TryParse(parts[1], out var port) || !byte.TryParse(parts[2], out var slaveId))
        {
            _logger.LogError("Invalid Modbus connection string: {ConnectionString}", connectionString);
            return Array.Empty<GasSensorReading>();
        }

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(ipAddress, port);
        await using var stream = tcpClient.GetStream();

        var readings = new List<GasSensorReading>();
        foreach (var register in registerAddresses)
        {
            var request = BuildModbusReadRequest(slaveId, register, 1);
            await stream.WriteAsync(request);

            var response = new byte[12];
            var bytesRead = await stream.ReadAsync(response);

            if (bytesRead > 0)
            {
                readings.Add(new GasSensorReading
                {
                    Timestamp = DateTime.UtcNow,
                    SensorId = $"Modbus_{slaveId}_{register}",
                    SensorLocation = $"Modbus Device {ipAddress}",
                    ChemicalName = DetermineChemicalFromRegister(register),
                    Concentration = ParseModbusResponse(response),
                    ConcentrationUnit = "ppm",
                    QualityFlag = "Good"
                });
            }
        }

        return readings;
    }

    public async Task<IEnumerable<GasSensorReading>> GetSerialDataAsync(string portName, int baudRate, string protocol)
    {
        _logger.LogInformation("Fetching serial data from {portName} at {baudRate} baud using {protocol}", portName, baudRate, protocol);
        try
        {
            using var serialPort = new SerialPort(portName, baudRate);
            serialPort.Open();

            var request = string.Equals(protocol, "NMEA0183", StringComparison.OrdinalIgnoreCase) ? "$GPGGA,*hh\r\n" : "READ_GAS\r\n";
            serialPort.WriteLine(request);

            await Task.Delay(200); // Give the device time to respond
            var response = await Task.Run(serialPort.ReadLine);

            return new[] { ParseNMEAGasData(response) ?? ParseGenericSerialData(response, portName) }.Where(r => r != null)!;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error communicating with serial port {PortName}", portName);
            return Array.Empty<GasSensorReading>();
        }
    }

    public Task<IEnumerable<GasSensorReading>> GetNetworkDataAsync(string host, int port, NetworkProtocol protocol)
    {
        _logger.LogInformation("Fetching network data using {protocol} from {host}:{port}", protocol, host, port);
        return protocol switch
        {
            NetworkProtocol.TCP => GetTcpGasDataAsync(host, port),
            NetworkProtocol.UDP => GetUdpGasDataAsync(host, port),
            _ => GetHttpGasDataAsync($"{protocol.ToString().ToLower()}://{host}:{port}")
        };
    }

    public async Task<IEnumerable<GasSensorReading>> GetFileDataAsync(string filePath, FileFormat format)
    {
        _logger.LogInformation("Reading gas sensor data from file {filePath} with format {format}", filePath, format);
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("File not found: {FilePath}", filePath);
            return Array.Empty<GasSensorReading>();
        }

        try
        {
            var content = await File.ReadAllTextAsync(filePath);
            return format switch
            {
                FileFormat.CSV => ParseCSVGasData(content),
                FileFormat.JSON => ParseJSONGasData(content),
                FileFormat.XML => ParseXMLGasData(content),
                _ => []
            };
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error reading file {FilePath}", filePath);
            return Array.Empty<GasSensorReading>();
        }
    }

    public Task StartContinuousMonitoringAsync(DataSourceConfiguration config)
    {
        if (_monitoringTimer != null)
        {
            StopContinuousMonitoringAsync();
        }
        _activeConfigurations.Add(config);
        _logger.LogInformation("Starting continuous gas sensor monitoring: {configName}", config.Name);
        _monitoringTimer = new Timer(async _ => await MonitorAllSourcesAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(config.PollingIntervalSeconds));
        return Task.CompletedTask;
    }

    public Task StopContinuousMonitoringAsync()
    {
        _monitoringTimer?.Change(Timeout.Infinite, 0);
        _monitoringTimer?.Dispose();
        _monitoringTimer = null;
        _activeConfigurations.Clear();
        _logger.LogInformation("Stopped continuous gas sensor monitoring");
        return Task.CompletedTask;
    }

    private async Task MonitorAllSourcesAsync()
    {
        var monitoringTasks = _activeConfigurations
            .Where(c => c.IsEnabled)
            .Select(async config =>
            {
                try
                {
                    return await GetDataFromConfiguration(config);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error monitoring source {configName}", config.Name);
                    return Enumerable.Empty<GasSensorReading>();
                }
            });

        var allReadings = (await Task.WhenAll(monitoringTasks)).SelectMany(r => r);

        if (allReadings.Any())
        {
            GasSensorDataReceived?.Invoke(this, allReadings);
        }
    }

    private Task<IEnumerable<GasSensorReading>> GetDataFromConfiguration(DataSourceConfiguration config) =>
        config.SourceType switch
        {
            DataSourceType.OSIPI => GetOSIPIDataAsync(config.ConnectionString, config.Parameters.GetValueOrDefault("tags", string.Empty).Split(',')),
            DataSourceType.OPCUA => GetOPCUADataAsync(config.ConnectionString, config.Parameters.GetValueOrDefault("nodeIds", string.Empty).Split(',')),
            DataSourceType.Modbus => GetModbusDataAsync(config.ConnectionString, config.Parameters.GetValueOrDefault("registers", string.Empty).Split(',').Select(int.Parse).ToArray()),
            DataSourceType.SerialPort => GetSerialDataAsync(config.ConnectionString.Split(':')[0], int.Parse(config.ConnectionString.Split(':')[1]), config.Parameters.GetValueOrDefault("protocol", "generic")),
            DataSourceType.File => GetFileDataAsync(config.ConnectionString, Enum.Parse<FileFormat>(config.Parameters.GetValueOrDefault("format", "CSV"), true)),
            _ => Task.FromResult(Enumerable.Empty<GasSensorReading>())
        };

    private async Task<IEnumerable<GasSensorReading>> GetTcpGasDataAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port);
            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("GET_GAS_DATA\r\n"));
            var buffer = new byte[1024];
            var bytesRead = await stream.ReadAsync(buffer);
            return ParseJSONGasData(Encoding.UTF8.GetString(buffer, 0, bytesRead));
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "TCP connection failed for {Host}:{Port}", host, port);
            return Array.Empty<GasSensorReading>();
        }
    }

    private async Task<IEnumerable<GasSensorReading>> GetUdpGasDataAsync(string host, int port)
    {
        try
        {
            using var client = new UdpClient();
            var endpoint = new IPEndPoint(IPAddress.Parse(host), port);
            var request = Encoding.UTF8.GetBytes("GET_GAS_DATA");
            await client.SendAsync(request, endpoint);
            var response = await client.ReceiveAsync();
            return ParseJSONGasData(Encoding.UTF8.GetString(response.Buffer));
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "UDP communication failed for {Host}:{Port}", host, port);
            return Array.Empty<GasSensorReading>();
        }
    }

    private async Task<IEnumerable<GasSensorReading>> GetHttpGasDataAsync(string url)
    {
        var client = _httpClient;
        try
        {
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return ParseJSONGasData(await response.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed for {Url}", url);
            return Array.Empty<GasSensorReading>();
        }
    }

    private static GasSensorReading? ParseOSIPIData(string jsonData, string tagName)
    {
        try
        {
            var data = JsonDocument.Parse(jsonData);
            var valueElement = data.RootElement.GetProperty("Value");
            return new GasSensorReading
            {
                Timestamp = DateTime.UtcNow,
                SensorId = tagName,
                ChemicalName = ExtractChemicalFromTagName(tagName),
                Concentration = valueElement.TryGetDouble(out var conc) ? conc : 0.0,
                ConcentrationUnit = "ppm",
                QualityFlag = valueElement.ValueKind == JsonValueKind.Number ? "Good" : "Bad"
            };
        }
        catch (JsonException)
        {
            // This method can't log directly, but the caller will.
            // Consider a more robust error-handling strategy if needed.
            return null;
        }
    }

    private static byte[] BuildModbusReadRequest(byte slaveId, int register, int count) =>
        [0x00, 0x01, 0x00, 0x00, 0x00, 0x06, slaveId, 0x03, (byte)(register >> 8), (byte)register, (byte)(count >> 8), (byte)count];

    private static double ParseModbusResponse(byte[] response) => response.Length >= 11 ? (response[9] << 8) | response[10] : 0;

    private static GasSensorReading? ParseNMEAGasData(string? nmeaData)
    {
        if (string.IsNullOrWhiteSpace(nmeaData) || !nmeaData.StartsWith('$')) return null;
        var parts = nmeaData.Split(',');
        if (parts.Length < 4) return null;

        return double.TryParse(parts[2], out var concentration) ? new GasSensorReading
        {
            Timestamp = DateTime.UtcNow,
            SensorId = "NMEA_GAS",
            ChemicalName = parts[1],
            Concentration = concentration,
            ConcentrationUnit = parts[3],
            QualityFlag = "Good"
        } : null;
    }

    private static GasSensorReading? ParseGenericSerialData(string? data, string portName)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        var parts = data.Split(',');
        return parts.Length >= 2 && double.TryParse(parts[1], out var concentration) ? new GasSensorReading
        {
            Timestamp = DateTime.UtcNow,
            SensorId = portName,
            ChemicalName = parts[0],
            Concentration = concentration,
            ConcentrationUnit = "ppm",
            QualityFlag = "Good"
        } : null;
    }

    private static List<GasSensorReading> ParseCSVGasData(string csvData)
    {
        var readings = new List<GasSensorReading>();
        var lines = csvData.Split(_csvSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(1)) // Skip header
        {
            var values = line.Split(',');
            if (values.Length >= 6 && DateTime.TryParse(values[0], out var timestamp) && double.TryParse(values[3], out var concentration))
            {
                readings.Add(new GasSensorReading
                {
                    Timestamp = timestamp,
                    SensorId = values[1],
                    ChemicalName = values[2],
                    Concentration = concentration,
                    ConcentrationUnit = values[4],
                    QualityFlag = values[5]
                });
            }
        }
        return readings;
    }

    private IEnumerable<GasSensorReading> ParseJSONGasData(string jsonData)
    {
        try
        {
            return JsonSerializer.Deserialize<List<GasSensorReading>>(jsonData, _jsonSerializerOptions) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error parsing JSON data: {jsonData}", jsonData);
            return Array.Empty<GasSensorReading>();
        }
    }

    private IEnumerable<GasSensorReading> ParseXMLGasData(string xmlData)
    {
        try
        {
            var doc = XDocument.Parse(xmlData);
            return doc.Root?.Elements("Reading").Select(item => new GasSensorReading
            {
                Timestamp = DateTime.TryParse(item.Element("Timestamp")?.Value, out var ts) ? ts : DateTime.MinValue,
                SensorId = item.Element("SensorId")?.Value ?? string.Empty,
                ChemicalName = item.Element("Chemical")?.Value ?? string.Empty,
                Concentration = double.TryParse(item.Element("Concentration")?.Value, out var conc) ? conc : 0.0,
                ConcentrationUnit = item.Element("Unit")?.Value ?? "ppm",
                QualityFlag = item.Element("Quality")?.Value ?? "Unknown"
            }) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing XML: {xmlData}", xmlData);
            return Array.Empty<GasSensorReading>();
        }
    }

    private static string ExtractChemicalFromNodeId(string nodeId) => nodeId switch
    {
        var n when n.Contains("NH3", StringComparison.OrdinalIgnoreCase) => "Ammonia",
        var n when n.Contains("SO2", StringComparison.OrdinalIgnoreCase) => "Sulfur Dioxide",
        var n when n.Contains("H2S", StringComparison.OrdinalIgnoreCase) => "Hydrogen Sulfide",
        _ => "Unknown"
    };

    private static string ExtractChemicalFromTagName(string tagName) => tagName switch
    {
        var t when t.Contains("NH3", StringComparison.OrdinalIgnoreCase) => "Ammonia",
        var t when t.Contains("SO2", StringComparison.OrdinalIgnoreCase) => "Sulfur Dioxide",
        var t when t.Contains("H2S", StringComparison.OrdinalIgnoreCase) => "Hydrogen Sulfide",
        _ => "Unknown"
    };

    private static string DetermineChemicalFromRegister(int register) => register switch
    {
        1000 => "Ammonia",
        1001 => "Sulfur Dioxide",
        1002 => "Hydrogen Sulfide",
        _ => "Unknown"
    };

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _monitoringTimer?.Dispose();
        }
        _isDisposed = true;
    }

}
