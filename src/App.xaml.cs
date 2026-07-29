using Dunhill.PrintStudio.Services;
using Dunhill.PrintStudio.Sync;
using Dunhill.PrintStudio.ViewModels;
using Dunhill.PrintStudio.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Windows;

namespace Dunhill.PrintStudio;

public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                // Singletons: print service owns the printer handle
                services.AddSingleton<PrintService>();

                // ViewModels (transient — fresh state per tab open)
                services.AddTransient<PrintViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<QueueViewModel>();
                services.AddTransient<HistoryViewModel>();
                services.AddTransient<InventoryViewModel>();

                // Views (need to be transient so each tab gets a fresh VM)
                services.AddTransient<PrintView>();
                services.AddTransient<SettingsView>();
                services.AddTransient<QueueView>();
                services.AddTransient<HistoryView>();
                services.AddTransient<InventoryView>();

                // Background services
                services.AddHttpClient("dunhill");
                services.AddSingleton(new SyncConfig());
                services.AddHostedService<CloudSyncService>();

                // Main window
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        // Phase 2: load settings from disk and pass to CloudSyncService
        // var cfg = JsonStore<AppSettings>.LoadAsync(DataPaths.Settings);

        var main = _host.Services.GetRequiredService<MainWindow>();
        main.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        using (_host) await _host.StopAsync();
        base.OnExit(e);
    }
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
