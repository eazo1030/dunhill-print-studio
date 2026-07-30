using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dunhill.PrintStudio.Services;
using Dunhill.PrintStudio.Usb;

namespace Dunhill.PrintStudio.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly PrintService _print;

    [ObservableProperty] private string tcpHost = "";
    [ObservableProperty] private int tcpPort = 9100;
    [ObservableProperty] private string browserPrintEndpoint = "http://127.0.0.1:888/postek/print";
    [ObservableProperty] private string? authToken = "";
    [ObservableProperty] private string cloudUrl = "https://dunhill-inventory-service.vercel.app";
    [ObservableProperty] private string status = "Disconnected";
    [ObservableProperty] private string? lastError;
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string? printerModel;
    [ObservableProperty] private string? connectionDetail;

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

    public SettingsViewModel(PrintService print)
    {
        _print = print;
        // Best-effort initial fill so the picker isn't empty on first load.
        try
        {
            foreach (var p in PostekSpoolerTransport.EnumeratePostekPrinters())
                SpoolerPrinters.Add(p);
            SelectedSpooler ??= SpoolerPrinters.FirstOrDefault();
        }
        catch { /* not Windows or no spooler; UI will surface Refresh */ }
    }

    public string ConnectionMode => _print.Status.ConnectionType switch
    {
        null => "Disconnected",
        var c when c.StartsWith("TCP") => "TCP",
        var c when c.StartsWith("Browser") => "Browser Print",
        var c when c.StartsWith("Spooler") => "Spooler",
        var c when c.StartsWith("USB") => "USB",
        _ => "Other"
    };

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
