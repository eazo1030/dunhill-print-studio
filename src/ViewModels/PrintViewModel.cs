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

    // v1.2.8 — fabric yardage layout fields (printed on the label).
    [ObservableProperty] private string fabricName = "";
    [ObservableProperty] private string yardage = "";
    [ObservableProperty] private string po = "";
    [ObservableProperty] private string datePrinted = "";

    public ObservableCollection<string> RecentJobs { get; } = new();

    // ZR300I is **300 DPI** (1 dot = 0.085 mm) — confirmed against the
    // Postek PPL API Manual v2.04 on 2026-07-31 after v1.2.4 through
    // v1.2.8 printed tiny text crammed into the upper-left because we
    // were passing 203-dpi dimensions and the printer framed the label
    // to those smaller bounds, then clipped the actual physical media.
    // 73 × 20 mm at 300 dpi = 862 × 236 dots.
    public LabelDimensions Dims { get; } = new(862, 236);

    partial void OnSkuChanged(string value) { /* form binding — preview removed in v1.2.7 */ }
    partial void OnItemNameChanged(string value) { /* same */ }
    partial void OnQuantityChanged(int value) { /* same */ }
    partial void OnSerialChanged(string? value) { /* same */ }
    partial void OnEpcChanged(string? value) { /* same */ }
    partial void OnFabricNameChanged(string value) { /* same */ }
    partial void OnYardageChanged(string value) { /* same */ }
    partial void OnPoChanged(string value) { /* same */ }
    partial void OnDatePrintedChanged(string value) { /* same */ }
    partial void OnEncodeRfidChanged(bool value) { /* same */ }

    // v1.2.7: PPLZ preview removed per operator request. The actual
    // print command stream still gets built server-side; the operator
    // sees the result on the physical label, not in a code pane.

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
            var spec = new LabelSpec(Sku, ItemName, Quantity, Serial,
                Epc: Epc, EncodeRfid: EncodeRfid,
                FabricName: FabricName, Yardage: Yardage,
                Po: Po, DatePrinted: DatePrinted);
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
        FabricName = ""; Yardage = ""; Po = ""; DatePrinted = "";
        Status = "Ready"; LastError = null;
    }
}
