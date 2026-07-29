using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dunhill.PrintStudio.Services;
using Dunhill.PrintStudio.Usb;

namespace Dunhill.PrintStudio.ViewModels;

public sealed record UsbDeviceInfo(string Path, ushort Vid, ushort Pid, string Description);

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

    /// <summary>Detected Postek ZR300I USB devices (VID 0x0FE6).</summary>
    public ObservableCollection<UsbDeviceInfo> UsbDevices { get; } = new();
    [ObservableProperty] private UsbDeviceInfo? selectedUsbDevice;
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
            var (paths, pidsByPath) = PostekUsbTransport.EnumeratePostekDevices(0x0FE6, _acceptPids);
            foreach (var p in paths)
            {
                var pid = pidsByPath.TryGetValue(p, out var v) ? v : (ushort)0;
                UsbDevices.Add(new UsbDeviceInfo(p, 0x0FE6, pid, $"Postek ZR300I (PID 0x{pid:X4})"));
            }
            if (UsbDevices.Count == 0)
            {
                LastError = _usb.LastError
                    ?? "No Postek ZR300I (VID 0x0FE6) found with WinUSB driver. Run Zadig, then click Scan again.";
            }
            else
            {
                LastError = null;
                SelectedUsbDevice ??= UsbDevices[0];
            }
        }
        finally
        {
            IsScanningUsb = false;
        }
    }

    private static readonly ushort[] _acceptPids = { 0x2012, 0x2024, 0x8150 };

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
