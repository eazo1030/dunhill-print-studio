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

    // 73mm × 20mm RFID inlay at 203 dpi (the operator's stock media):
    //   width  = 73 mm * 203/25.4 ≈ 583 dots
    //   height = 20 mm * 203/25.4 ≈ 160 dots
    // PTK_SetLabelHeight will use (160, 24, 0, false) on every Browser Print
    // job, which is the right shape for this stock. Change the constructor
    // below if you switch media later.
    public LabelDimensions Dims { get; } = new(583, 160);

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
            PrintJobOutcome outcome;
            try
            {
                outcome = await _print.PrintLabelJobAsync(spec, Dims, Epc);
            }
            catch (Exception ex)
            {
                LastError = $"Print threw: {ex.Message}";
                Status = "Print failed";
                return;
            }

            // Compact, human-readable outcome line.
            LastError = outcome.Error;
            switch (outcome.Status)
            {
                case PrintJobStatus.Done:
                    var readback = string.IsNullOrEmpty(outcome.ReadbackHex)
                        ? ""
                        : $" (EPC verify: {outcome.ReadbackHex})";
                    Status = $"Printed {Quantity}× {Sku} at {DateTime.Now:HH:mm:ss}{readback}";
                    RecentJobs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {Sku} ×{Quantity}  ✓");
                    if (RecentJobs.Count > 20) RecentJobs.RemoveAt(20);
                    LastError = null;
                    break;
                case PrintJobStatus.VoidLabel:
                    // Void-on-fail: the printer marked the label with a VOID overlay
                    // (or our job emits a VOID rectangle when verify fails). The
                    // inventory must NOT be incremented for this SKU.
                    Status = $"VOIDED {Sku} — EPC didn't verify ({outcome.Error ?? "read mismatch"})";
                    RecentJobs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {Sku} ×{Quantity}  ✗ VOID");
                    if (RecentJobs.Count > 20) RecentJobs.RemoveAt(20);
                    break;
                case PrintJobStatus.Failed:
                default:
                    Status = $"Print failed: {outcome.Error ?? "(unknown)"}";
                    RecentJobs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {Sku} ×{Quantity}  ✗ FAILED");
                    if (RecentJobs.Count > 20) RecentJobs.RemoveAt(20);
                    break;
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
