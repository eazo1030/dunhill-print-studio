using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dunhill.PrintStudio.Models;
using Dunhill.PrintStudio.Usb;
using System.Collections.ObjectModel;

namespace Dunhill.PrintStudio.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly PrintService _print;

    public SettingsViewModel(PrintService print) { _print = print; }

    [ObservableProperty] private string connectionType = "USB";   // "USB" or "TCP"
    [ObservableProperty] private string tcpHost = "";
    [ObservableProperty] private int tcpPort = 9100;
    [ObservableProperty] private string? authToken = "";
    [ObservableProperty] private string cloudUrl = "https://dunhill-inventory-service.vercel.app";
    [ObservableProperty] private string status = "Disconnected";
    [ObservableProperty] private string? lastError;
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string? printerModel;
    [ObservableProperty] private string? connectionDetail;

    public ObservableCollection<PostekDeviceInfo> UsbDevices { get; } = new();

    [RelayCommand]
    private void RefreshUsb()
    {
        UsbDevices.Clear();
        foreach (var d in _print.DiscoverUsb()) UsbDevices.Add(d);
        Status = UsbDevices.Count == 0
            ? "No Postek printers found on USB"
            : $"Found {UsbDevices.Count} device(s)";
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        bool ok = ConnectionType == "USB"
            ? _print.ConnectUsb()
            : _print.ConnectTcp(TcpHost, TcpPort);

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
        var dims = new LabelDimensions(812, 1218);
        var ok = await _print.PrintTestLabelAsync(dims);
        Status = ok ? "Test label printed" : $"Test failed: {_print.LastError}";
    }
}
