using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace Dunhill.PrintStudio.ViewModels;

/// <summary>
/// Designer tab view-model — v1.2.5.
///
/// The label layout is FIXED: Fabric Name / Yardage / PO + Date Printed.
/// No templates, no undo, no drag-and-drop. The view is a fill-in form
/// with a live PPLZ preview so the operator can see the exact command
/// stream about to be sent.
///
/// The four field values drive a label of 73×20mm at 203 dpi (583×160
/// dots). Default values are filled in so the preview isn't blank.
/// </summary>
public partial class DesignerViewModel : ObservableObject
{
    // 73×20mm at 203 dpi → 583×160 dots. matches PrintViewModel.Dims.
    private const int WidthDots  = 583;
    private const int HeightDots = 160;

    [ObservableProperty] private string fabricName = "Sample Cotton";
    [ObservableProperty] private string yardage    = "12.5 yds";
    [ObservableProperty] private string po         = "PO-0001";

    /// <summary>
    /// Editable date stamp (mm/dd/yyyy). Defaults to today. Bound to the
    /// "Today" reset button via <see cref="UseTodayCommand"/>.
    /// </summary>
    [ObservableProperty] private string datePrinted = "";

    /// <summary>Computed preview string — fed to the XAML TextBox.</summary>
    [ObservableProperty] private string labelPreview = "";

    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private string? lastError;

    public DesignerViewModel()
    {
        DatePrinted = DateTime.Now.ToString("MM/dd/yyyy");
        UpdatePreview();
    }

    partial void OnFabricNameChanged(string value) => UpdatePreview();
    partial void OnYardageChanged(string value)    => UpdatePreview();
    partial void OnPoChanged(string value)         => UpdatePreview();
    partial void OnDatePrintedChanged(string value) => UpdatePreview();

    /// <summary>
    /// Text shown on the preview's footer: prefix the user-entered date
    /// with "Date Printed: ". Defaults to a placeholder when blank so the
    /// preview area never collapses.
    /// </summary>
    public string DatePrintedFooter =>
        string.IsNullOrWhiteSpace(DatePrinted)
            ? "Date Printed: mm/dd/yyyy"
            : $"Date Printed: {DatePrinted}";

    [RelayCommand]
    private void UseToday()
    {
        DatePrinted = DateTime.Now.ToString("MM/dd/yyyy");
        Status = "Date reset to today.";
    }

    /// <summary>
    /// Build the live PPLZ preview for the locked layout using only the
    /// four visible fields. Matches the actual printer output for the
    /// non-Browser-Print transports (TCP / Spooler / USB) — those paths
    /// use the raw PPLZ returned from this method.
    /// </summary>
    /// <remarks>
    /// Coordinate choices (dots, 0=label top-left, 73×20mm @ 203 dpi):
    ///   Fabric Name  : x=14,  y=10  h=30, dark blue.
    ///   Yardage      : x=14,  y=46  h=56, bold, the headline.
    ///   divider line : x=14,  y=106 w=555 th=2.
    ///   PO           : x=14,  y=112 h=22.
    ///   Date Printed : x=14,  y=140 h=14, light grey, bottom-left.
    ///
    /// Heading / divider / footer are ASCII-safe; values are escaped
    /// through <see cref="PplzBuilder.Escape"/> if any field contains
    /// a caret / tilde.
    /// </remarks>
    private void UpdatePreview()
    {
        try
        {
            var sb = new System.Text.StringBuilder(512);

            sb.Append("^XA")
              .Append("^CI28")
              .Append("^PW").Append(WidthDots)
              .Append("^LL").Append(HeightDots)
              .Append("^LH0,0")
              .Append("^MD8")
              .Append("^PR4");

            // --- Fabric Name ---
            sb.Append("^FO14,10^A0N,30,30^FD")
              .Append(EscapePplz($"Fabric: {(string.IsNullOrWhiteSpace(FabricName) ? "" : FabricName)}"))
              .Append("^FS");

            // --- Yardage (the headline) ---
            sb.Append("^FO14,46^A0N,56,56^FD")
              .Append(EscapePplz((string.IsNullOrWhiteSpace(Yardage) ? "" : Yardage)))
              .Append("^FS");

            // --- Divider ---
            sb.Append("^FO14,106^GB555,2,2,B^FS");

            // --- PO ---
            sb.Append("^FO14,114^A0N,22,22^FD")
              .Append(EscapePplz((string.IsNullOrWhiteSpace(Po) ? "" : Po)))
              .Append("^FS");

            // --- Date Printed (small, bottom) ---
            sb.Append("^FO14,140^A0N,14,14^FD")
              .Append(EscapePplz($"Date Printed: {(string.IsNullOrWhiteSpace(DatePrinted) ? "" : DatePrinted)}"))
              .Append("^FS");

            sb.Append("^XZ");

            LabelPreview = sb.ToString();
            Status = "Ready";
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = "Preview failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Escape PPLZ field data: caret, tilde, backslash.
    /// Mirror of PplzBuilder.Escape (kept local so this VM doesn't
    /// depend on PplzBuilder internals).
    /// </summary>
    private static string EscapePplz(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("\\", "\\\\")
                .Replace("^",  "\\5E")
                .Replace("~",  "\\7E");
    }
}
