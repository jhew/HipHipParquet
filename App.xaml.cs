using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using HipHipParquet.Services;
using HipHipParquet.Views;

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
        Startup += OnStartup;

        try
        {
            // Setup DI
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // Note: ParquetService is created per-load in MainWindow and is not a singleton.
                    services.AddSingleton<QualityScoreService>();
                    services.AddSingleton<NarrativeService>();
                    services.AddSingleton<ReportService>();
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

    private void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            // Create the main window
            var mainWindow = new MainWindow();
            
            // Check if a file path was passed as a command-line argument
            if (e.Args.Length > 0)
            {
                var arg0 = e.Args[0];
                if (string.Equals(arg0, "--compare-with-last", StringComparison.OrdinalIgnoreCase))
                {
                    _ = mainWindow.QueueStartupCommandAsync("compare-with-last");
                }
                else if (string.Equals(arg0, "--open-latest-report", StringComparison.OrdinalIgnoreCase))
                {
                    TryOpenLatestReport();
                }
                else if (!string.Equals(arg0, "--restore-workspace", StringComparison.OrdinalIgnoreCase))
                {
                    var filePath = System.IO.Path.GetFullPath(arg0);
                    if (System.IO.File.Exists(filePath))
                        _ = mainWindow.LoadFileFromCommandLineAsync(filePath);
                }
            }
            
            // Show the window
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup error: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void TryOpenLatestReport()
    {
        try
        {
            var pointerPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HipHipParquet",
                "latest-report.txt");

            if (!System.IO.File.Exists(pointerPath))
                return;

            var reportPath = System.IO.File.ReadAllText(pointerPath).Trim();
            if (!string.IsNullOrWhiteSpace(reportPath) && System.IO.File.Exists(reportPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = reportPath,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Ignore quick-action launch failures.
        }
    }
}
