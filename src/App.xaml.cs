using System.IO;
using System.Windows;
using Dunhill.PrintStudio.Services;
using Dunhill.PrintStudio.Sync;
using Dunhill.PrintStudio.Usb;
using Dunhill.PrintStudio.ViewModels;
using Dunhill.PrintStudio.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dunhill.PrintStudio;

public partial class App : Application
{
    private readonly IHost _host;

    /// <summary>
    /// Static accessor for the DI service provider. Used by views that need
    /// to resolve their ViewModels without going through constructor injection
    /// (since WPF's XAML loader uses the default parameterless constructor).
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        // Log every step of startup so silent failures have a paper trail.
        // WPF WinExe has no console — file logging is the only way to see what happened.
        Log("App.ctor start");

        try
        {
            Log($"Elevation: running with admin token = {IsElevated()}");

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((_, services) =>
                {
                    Log("ConfigureServices: PrintService");
                    services.AddSingleton<PrintService>();
                    // Spooler transport does its work via static enumerators + the
                    // short-lived wrapper inside PrintService.ConnectSpooler(); no
                    // singleton needed. WinUSB transport is left registered only
                    // if it's referenced (future use); it's currently unused from
                    // the UI so we skip the DI registration.
                    if (false)
                    {
                        services.AddSingleton<PostekUsbTransport>();
                    }

                    Log("ConfigureServices: ViewModels");
                    services.AddTransient<PrintViewModel>();
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<QueueViewModel>();
                    services.AddTransient<HistoryViewModel>();
                    services.AddTransient<InventoryViewModel>();

                    Log("ConfigureServices: Views");
                    services.AddTransient<PrintView>();
                    services.AddTransient<SettingsView>();
                    services.AddTransient<QueueView>();
                    services.AddTransient<HistoryView>();
                    services.AddTransient<InventoryView>();

                    Log("ConfigureServices: HttpClient + Sync");
                    services.AddHttpClient("dunhill");
                    services.AddSingleton(new SyncConfig());
                    services.AddHostedService<CloudSyncService>();

                    Log("ConfigureServices: MainWindow");
                    services.AddSingleton<MainWindow>();
                })
                .Build();
            Services = _host.Services;
            Log("Host built OK");
        }
        catch (Exception ex)
        {
            Log("Host build FAILED: " + ex);
            throw;
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        Log("OnStartup start");
        try
        {
            await _host.StartAsync();
            Log("Host started");

            var main = _host.Services.GetRequiredService<MainWindow>();
            Log("MainWindow resolved, calling Show()");
            main.Show();
            Log("MainWindow shown");

            base.OnStartup(e);
            Log("OnStartup complete");
        }
        catch (Exception ex)
        {
            Log("OnStartup FAILED: " + ex);
            MessageBox.Show(
                "Failed to start Dunhill Print Studio:\n\n" + ex.Message +
                "\n\nDetails written to " + LogPath,
                "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log("OnExit");
        using (_host) await _host.StopAsync();
        base.OnExit(e);
    }

    // ---- file-based startup logging ----
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DunhillPrintStudio",
        "startup.log");

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { /* swallow — logging must never crash startup */ }
    }

    private static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(id);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
