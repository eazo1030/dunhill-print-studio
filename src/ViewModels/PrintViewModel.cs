using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dunhill.PrintStudio.Models;
using Dunhill.PrintStudio.Pplz;
using Dunhill.PrintStudio.Services;
using Dunhill.PrintStudio.Usb;
using System.Collections.ObjectModel;

namespace Dunhill.PrintStudio.ViewModels;

public partial class PrintViewModel : ObservableObject
{
    private readonly PrintService _print;

    public PrintViewModel(PrintService print) { _print = print; }

    [ObservableProperty] private string sku = "";
    [ObservableProperty] private string itemName = "";
    [ObservableProperty] private int quantity = 1;
    [ObservableProperty] private string? serial;
    [ObservableProperty] private string? epc;
    [ObservableProperty] private bool encodeRfid;
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private string? lastError;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string labelPreview = "";

    public ObservableCollection<string> RecentJobs { get; } = new();

    // Standard 4" x 6" at 203 dpi = 812 x 1218 dots (matches ZR300I default)
    public LabelDimensions Dims { get; } = new(812, 1218);

    partial void OnSkuChanged(string value) => UpdatePreview();
    partial void OnItemNameChanged(string value) => UpdatePreview();
    partial void OnQuantityChanged(int value) => UpdatePreview();
    partial void OnSerialChanged(string? value) => UpdatePreview();
    partial void OnEpcChanged(string? value) => UpdatePreview();

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(Sku))
        {
            LabelPreview = "(enter SKU to preview)";
            return;
        }
        var spec = new LabelSpec(Sku, ItemName, Quantity, Serial, EncodeRfid: EncodeRfid, Epc: Epc);
        LabelPreview = PplzBuilder.BuildItemLabel(spec, Dims);
    }

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (string.IsNullOrWhiteSpace(Sku))
        {
            LastError = "SKU is required.";
            Status = "Validation failed";
            return;
        }
        if (!_print.Status.Online)
        {
            LastError = "Printer offline. Click Connect in Settings.";
            Status = "Offline";
            return;
        }

        IsBusy = true;
        Status = "Printing…";
        try
        {
            var spec = new LabelSpec(Sku, ItemName, Quantity, Serial, Epc: Epc, EncodeRfid: EncodeRfid);
            var ok = await _print.PrintAsync(spec, Dims);
            if (ok)
            {
                Status = $"Printed {Quantity}x {Sku} at {DateTime.Now:HH:mm:ss}";
                LastError = null;
                RecentJobs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {Sku} x{Qty}");
                if (RecentJobs.Count > 20) RecentJobs.RemoveAt(20);
            }
            else
            {
                LastError = _print.LastError;
                Status = "Print failed";
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ClearForm()
    {
        Sku = ""; ItemName = ""; Quantity = 1; Serial = null; Epc = null; EncodeRfid = false;
        Status = "Ready"; LastError = null;
    }
}
