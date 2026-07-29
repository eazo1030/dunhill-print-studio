using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dunhill.PrintStudio.Services;
using Dunhill.PrintStudio.Usb;

namespace Dunhill.PrintStudio.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly PrintService _print;
    private readonly PostekUsbTransport _usb;

    [ObservableProperty] private string tcpHost = "";
    [ObservableProperty] private int tcpPort = 9100;
    [ObservableProperty] private string? authToken = "";
    [ObservableProperty] private string cloudUrl = "https://dunhill-inventory-service.vercel.app";
    [ObservableProperty] private string status = "Disconnected";
    [ObservableProperty] private string? lastError;
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string? printerModel;
    [ObservableProperty] private string? connectionDetail;

    /// <summary>Every USB device visible to Win32 on this machine.</summary>
    public ObservableCollection<PostekUsbTransport.UsbDeviceInfo> UsbDevices { get; } = new();
    [ObservableProperty] private PostekUsbTransport.UsbDeviceInfo? selectedUsbDevice;
    [ObservableProperty] private bool isScanningUsb;

    public SettingsViewModel(PrintService print, PostekUsbTransport usb)
    {
        _print = print;
        _usb = usb;
    }

    public string ConnectionMode => _print.Status.ConnectionType?.StartsWith("USB") == true ? "USB" : "TCP";

    [RelayCommand]
    private void ScanUsb()
    {
        if (IsScanningUsb) return;
        IsScanningUsb = true;
        try
        {
            UsbDevices.Clear();
            var all = PostekUsbTransport.EnumerateAllUsbDevices();
            Log($"Scan: EnumerateAllUsbDevices returned {all.Count} devices");
            foreach (var d in all)
            {
                Log($"  device: vid=0x{d.Vid:X4} pid=0x{d.Pid:X4} name='{d.Name}' path='{d.Path}'");
                UsbDevices.Add(d);
            }

            if (UsbDevices.Count == 0)
            {
                LastError = "No USB devices found. " +
                             "If your printer is plugged in, try right-click → Run as Administrator on the .exe, then Scan again.";
            }
            else
            {
                LastError = null;
                var firstPostek = UsbDevices.FirstOrDefault(d =>
                    d.Vid == PostekUsbTransport.PostekVendorId);
                SelectedUsbDevice = firstPostek ?? UsbDevices[0];
            }
        }
        finally
        {
            IsScanningUsb = false;
        }
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

    [RelayCommand]
    private void ConnectUsb()
    {
        if (SelectedUsbDevice is null)
        {
            LastError = "Pick a USB device first, or click Scan.";
            Status = "Need device";
            return;
        }
        var ok = _print.ConnectUsbPath(SelectedUsbDevice.Path);
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
            LastError = _print.LastError ?? "USB connect failed.";
            Status = "USB connection failed";
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
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
}
