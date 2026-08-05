using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dunhill.PrintStudio.Services;
using Dunhill.PrintStudio.Usb;
using Velopack;
using Velopack.Exceptions;

namespace Dunhill.PrintStudio.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly PrintService _print;
    private readonly UpdateService _update;

    [ObservableProperty] private string tcpHost = "";
    [ObservableProperty] private int tcpPort = 9100;
    [ObservableProperty] private string browserPrintEndpoint = "http://127.0.0.1:888/postek/print";
    [ObservableProperty] private string? authToken = "ec4c38b989417e2c55f48d4c7b4122074a5318c6b3c51bbba813d1509bf62c0f";
    [ObservableProperty] private string cloudUrl = "https://dunhill-inventory-service.vercel.app";
    [ObservableProperty] private string status = "Disconnected";
    [ObservableProperty] private string? lastError;
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string? printerModel;
    [ObservableProperty] private string? connectionDetail;

    // Update UI state — exposed so the Settings panel can show "Up to date"
    // / "v1.0.1 available" / "Downloading 42%…" / "Restarting…" without the
    // operator having to read startup.log.
    [ObservableProperty] private string currentVersion = "0.0.0";
    [ObservableProperty] private string? availableVersion;
    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private bool isCheckingUpdate;
    [ObservableProperty] private bool isDownloadingUpdate;
    [ObservableProperty] private string? updateStatusMessage;

    /// <summary>Installed Postek print queues, refreshed by RefreshSpooler.</summary>
    public ObservableCollection<PostekSpoolerTransport.SpoolerPrinterInfo> SpoolerPrinters { get; } = new();
    [ObservableProperty] private PostekSpoolerTransport.SpoolerPrinterInfo? selectedSpooler;
    [ObservableProperty] private bool isScanningSpooler;

    [RelayCommand]
    private void RefreshSpooler()
    {
        if (IsScanningSpooler) return;
        IsScanningSpooler = true;
        try
        {
            SpoolerPrinters.Clear();
            var all = PostekSpoolerTransport.EnumeratePostekPrinters();
            Log($"RefreshSpooler: {all.Count} Postek queue(s) found");
            foreach (var p in all)
            {
                Log($"  queue: name='{p.Name}' driver='{p.DriverName}' port='{p.PortName}' jobs={p.JobCount}");
                SpoolerPrinters.Add(p);
            }
            SelectedSpooler ??= SpoolerPrinters.FirstOrDefault();
            if (SpoolerPrinters.Count == 0)
                LastError = "No Postek print queue found. " +
                             "Click 'Add printer' from Windows Settings to install the Seagull driver for the ZR300I, then Refresh.";
            else
                LastError = null;
        }
        finally { IsScanningSpooler = false; }
    }

    [RelayCommand]
    private void ConnectSpooler()
    {
        if (SelectedSpooler is null)
        {
            LastError = "Pick a Postek printer from the list, or click Refresh.";
            Status = "Need printer";
            return;
        }
        var ok = _print.ConnectSpooler(SelectedSpooler.Name);
        if (ok)
        {
            IsConnected = true;
            PrinterModel = _print.Status.Model;
            ConnectionDetail = _print.Status.ConnectionType;
            Status = $"Connected: {PrinterModel}";
            LastError = null;
        }
        else
        {
            IsConnected = false;
            LastError = _print.LastError ?? "Spooler connect failed.";
            Status = "Spooler connection failed";
        }
    }

    public SettingsViewModel(PrintService print, UpdateService update)
    {
        _print = print;
        _update = update;
        CurrentVersion = _update.CurrentVersion;
        // Mirror the print service's status events into our own observable
        // properties. Without this hook the toolbar's "Connected: …" line and
        // the Mode label stay stale until the user changes another input.
        _print.StatusChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(ConnectionDetail));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(PrinterModel));
            OnPropertyChanged(nameof(ConnectionMode));
        };
        // Best-effort initial fill so the picker isn't empty on first load.
        try
        {
            foreach (var p in PostekSpoolerTransport.EnumeratePostekPrinters())
                SpoolerPrinters.Add(p);
            SelectedSpooler ??= SpoolerPrinters.FirstOrDefault();
        }
        catch { /* not Windows or no spooler; UI will surface Refresh */ }
    }

    /// <summary>
    /// Convert <see cref="PrintService.Status.ConnectionType"/> to a
    /// short human-readable mode label. Raised as
    /// <see cref="CommunityToolkit.Mvvm.ComponentModel.PropertyChangedEventArgs"/>
    /// via the &lt;Mode /&gt; binding in the status strip.
    /// </summary>
    public string ConnectionMode
    {
        get
        {
            var c = _print.Status.ConnectionType ?? "";
            if (string.IsNullOrEmpty(c)) return "Disconnected";
            if (c.StartsWith("TCP")) return "TCP";
            if (c.StartsWith("Browser")) return "Browser Print";
            if (c.StartsWith("Spooler")) return "Spooler";
            if (c.StartsWith("USB")) return "USB";
            return "Other";
        }
    }

    [RelayCommand]
    private async Task ConnectTcpAsync()
    {
        if (string.IsNullOrWhiteSpace(TcpHost))
        {
            LastError = "Enter a hostname or IP for the printer.";
            Status = "Need host";
            return;
        }
        var ok = _print.ConnectTcp(TcpHost, TcpPort);
        if (ok)
        {
            IsConnected = true;
            PrinterModel = _print.Status.Model;
            ConnectionDetail = _print.Status.ConnectionType;
            Status = $"Connected: {PrinterModel}";
            LastError = null;
        }
        else
        {
            IsConnected = false;
            LastError = _print.LastError;
            Status = "Connection failed";
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ConnectBrowserPrintAsync()
    {
        var url = string.IsNullOrWhiteSpace(BrowserPrintEndpoint)
            ? "http://127.0.0.1:888/postek/print"
            : BrowserPrintEndpoint.Trim();
        var ok = _print.ConnectBrowserPrint(url);
        if (ok)
        {
            IsConnected = true;
            PrinterModel = _print.Status.Model;
            ConnectionDetail = _print.Status.ConnectionType;
            Status = $"Connected: {PrinterModel}";
            LastError = null;
        }
        else
        {
            IsConnected = false;
            LastError = _print.LastError;
            Status = "Browser Print connection failed";
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task TestBrowserPrint()
    {
        var ok = await _print.BrowserPrintProbeAsync();
        Status = ok ? "Browser Print probe OK" : $"Browser Print probe failed: {_print.LastError}";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void Disconnect()
    {
        _print.Disconnect();
        IsConnected = false;
        Status = "Disconnected";
    }

    [RelayCommand]
    private async Task PrintTestLabelAsync()
    {
        if (!_print.Status.Online)
        {
            LastError = "Connect first.";
            return;
        }
        var dims = new Pplz.LabelDimensions(812, 1218);
        var ok = await _print.PrintTestLabelAsync(dims);
        Status = ok ? "Test label printed" : $"Test failed: {_print.LastError}";
    }

    // ---------------------------------------------------------------------
    // Update commands — driven from Settings → "Check for updates" button.
    // Flow: Check → if newer version → Download → ApplyUpdatesAndExit
    // (which exits the process; Velopack then relaunches the new version).
    // ---------------------------------------------------------------------
    private UpdateInfo? _pendingUpdate;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdate))]
    private async Task CheckForUpdateAsync()
    {
        if (IsCheckingUpdate || IsDownloadingUpdate) return;
        IsCheckingUpdate = true;
        UpdateStatusMessage = "Checking for updates…";
        LastError = null;
        try
        {
            var info = await _update.CheckForUpdatesAsync();
            if (info is null)
            {
                UpdateAvailable = false;
                AvailableVersion = null;
                UpdateStatusMessage = $"Up to date (v{CurrentVersion}).";
            }
            else
            {
                _pendingUpdate = info;
                UpdateAvailable = true;
                AvailableVersion = info.TargetFullRelease.Version?.ToString();
                UpdateStatusMessage = $"v{AvailableVersion} is available.";
            }
        }
        catch (NotInstalledException)
        {
            // Running from raw .exe (dev box, CI artifact, first install) — no
            // Velopack install to update. Show a helpful hint, don't blow up.
            UpdateStatusMessage =
                "Self-update is only available when installed via Velopack. " +
                "Run the published installer, not the raw .exe.";
        }
        catch (Exception ex)
        {
            LastError = "Update check failed: " + ex.Message;
            UpdateStatusMessage = "Update check failed.";
        }
        finally
        {
            IsCheckingUpdate = false;
            CheckForUpdateCommand.NotifyCanExecuteChanged();
            DownloadAndRestartCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCheckForUpdate() => !IsCheckingUpdate && !IsDownloadingUpdate;

    [RelayCommand(CanExecute = nameof(CanDownloadAndRestart))]
    private async Task DownloadAndRestartAsync()
    {
        if (_pendingUpdate is null || IsDownloadingUpdate) return;
        IsDownloadingUpdate = true;
        UpdateStatusMessage = "Downloading update…";
        LastError = null;
        try
        {
            await _update.DownloadUpdatesAsync(_pendingUpdate);
            UpdateStatusMessage = "Restarting to apply update…";
            // ApplyUpdatesAndExit calls ExitProcess internally — control does
            // not return. The new version launches once we exit.
            _update.ApplyUpdatesAndExit(_pendingUpdate);
        }
        catch (Exception ex)
        {
            LastError = "Update download failed: " + ex.Message;
            UpdateStatusMessage = "Update download failed.";
            IsDownloadingUpdate = false;
            DownloadAndRestartCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanDownloadAndRestart() =>
        UpdateAvailable && _pendingUpdate is not null && !IsDownloadingUpdate;

    private static void Log(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DunhillPrintStudio");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "startup.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { /* swallow */ }
    }
}
