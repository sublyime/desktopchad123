using System.Configuration;
using System.Data;
using System.Windows;
using System.Net.Http;
using ChemicalDispersionModeling.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using ChemicalDispersionModeling.Desktop.ViewModels;
using ChemicalDispersionModeling.Core.Services;
using ChemicalDispersionModeling.Core.Services.Implementation;
using ChemicalDispersionModeling.Data.Context;

namespace ChemicalDispersionModeling.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    public static IServiceProvider ServiceProvider { get; private set; } = default!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        Console.WriteLine("=== Application OnStartup ===");
        // Create host builder
        var hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: true);
                config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true);
            })
            .ConfigureServices((context, services) =>
            {
                ConfigureServices(services, context.Configuration);
            })
            .ConfigureLogging(logging =>
            {
                logging.AddConsole();
                logging.AddDebug();
            });

        _host = hostBuilder.Build();

        // Add global exception handler
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            MessageBox.Show($"Unhandled exception: {exception?.Message}\n\nStack trace:\n{exception?.StackTrace}", 
                "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (sender, e) =>
        {
            MessageBox.Show($"Dispatcher exception: {e.Exception.Message}\n\nStack trace:\n{e.Exception.StackTrace}", 
                "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        try
        {
            Console.WriteLine("Starting host...");
            await _host.StartAsync();
            Console.WriteLine("Host started successfully.");
            
            // Set the static ServiceProvider
            ServiceProvider = _host.Services;
            
            // Create and show main window
            Console.WriteLine("Getting MainWindow service...");
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            Console.WriteLine("Getting MainViewModel service...");
            var viewModel = _host.Services.GetRequiredService<MainViewModel>();
            Console.WriteLine("Setting DataContext...");
            mainWindow.DataContext = viewModel;
            Console.WriteLine("Showing main window...");
            mainWindow.Show();
            Console.WriteLine("MainWindow.Show() completed.");
            
            base.OnStartup(e);
            Console.WriteLine("OnStartup completed.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Application startup failed: {ex.Message}", "Startup Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }

    private void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Database configuration
        services.AddDbContext<ChemicalDispersionContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection") ??
                "Host=localhost;Database=javadisp;Username=postgres;Password=CHANGE_ME_DB_PASSWORD";
            options.UseNpgsql(connectionString);
        });

        // Core Services
        services.AddScoped<IWeatherService, WeatherService>();
        services.AddScoped<ITerrainService, TerrainService>();
        services.AddScoped<IRealMappingService, RealMappingService>();
        services.AddScoped<IDispersionModelingService, DispersionModelingService>();

        // Real Data Services
        services.AddScoped<ILiveWeatherService, Core.Services.Implementation.LiveWeatherService>();
        services.AddScoped<IGasSensorService, GasSensorService>();

        // HTTP Client for weather and gas sensor services
        services.AddHttpClient<ILiveWeatherService, Core.Services.Implementation.LiveWeatherService>();
        services.AddHttpClient("OSIPI");

        // ViewModels
        services.AddTransient<MainViewModel>();

        // Windows
        services.AddTransient<MainWindow>();
    }
}
