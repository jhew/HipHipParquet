using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using HipHipParquet.Services;

namespace HipHipParquet;

public partial class App : Application
{
    private IHost _host;
    public IServiceProvider Services => _host.Services;
    public new static App Current => (App)Application.Current;

    public App()
    {
        // Add global exception handling
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            // Setup DI
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<ParquetService>();
                })
                .Build();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"App setup error: {ex}");
            _host = Host.CreateDefaultBuilder().Build();
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"An error occurred: {e.Exception.Message}\n\nClick OK to continue.", 
                       "Application Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // Prevent app crash
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        MessageBox.Show($"A fatal error occurred: {ex?.Message}", 
                       "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
