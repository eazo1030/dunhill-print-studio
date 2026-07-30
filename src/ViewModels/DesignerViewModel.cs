using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dunhill.PrintStudio.Models;
using Dunhill.PrintStudio.Pplz;
using Dunhill.PrintStudio.Services;

namespace Dunhill.PrintStudio.ViewModels;

/// <summary>
/// View-model for the Label Designer tab. Lets the operator:
///   • pick / save / delete label templates (stored as JSON in
///     %LOCALAPPDATA%\DunhillPrintStudio\templates\)
///   • add Text / Barcode / QR / RFID / Line / Box elements
///   • drag-position elements on a preview canvas (XY edits via numeric boxes)
///   • preview the rendered PPLZ for the current field values
/// </summary>
public partial class DesignerViewModel : ObservableObject
{
    private readonly TemplateStore _store;

    /// <summary>All templates loaded from disk.</summary>
    public ObservableCollection<LabelTemplate> Templates { get; } = new();

    [ObservableProperty] private LabelTemplate? selectedTemplate;

    /// <summary>Elements of the currently selected template (view-binding).</summary>
    public ObservableCollection<LabelElement> Elements { get; } = new();

    [ObservableProperty] private LabelElement? selectedElement;
    [ObservableProperty] private string? lastError;
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private string labelPreview = "";

    // Field bindings — the operator types sample values here to see a live preview
    [ObservableProperty] private string sampleSku = "ABC-001";
    [ObservableProperty] private string sampleName = "Widget Pro";
    [ObservableProperty] private string sampleSerial = "SN12345";
    [ObservableProperty] private string sampleEpc = "E200 3411 B802 0117 6200 17AC";
    [ObservableProperty] private int sampleQty = 1;

    public DesignerViewModel(TemplateStore store)
    {
        _store = store;
        // Seed with a built-in starter template so a fresh install has something
        // to print without first designing one. Loaded from Templates/seed.json
        // if present, otherwise synthesized.
        SeedBuiltInDefaults();
        Reload();
        if (Templates.Count > 0) SelectedTemplate = Templates[0];
    }

    private void SeedBuiltInDefaults()
    {
        // 73x20mm RFID inlay at 203 DPI — the operator's stock media
        var starter = new LabelTemplate
        {
            Id = "builtin-73x20-rfid",
            Name = "73×20mm RFID Inlay (starter)",
            WidthMm = 73, HeightMm = 20, Dpi = 203,
            Darkness = 8, PrintSpeed = 4, GapDots = 24,
            Elements =
            {
                new LabelElement { Type = "text", X = 20, Y = 20,
                                   Font = "0", FontHeight = 36, FontWidth = 36,
                                   Content = "DUNHILL-GLOBAL" },
                new LabelElement { Type = "text", X = 20, Y = 64,
                                   Font = "0", FontHeight = 56, FontWidth = 56,
                                   Field = "Sku", Prefix = "SKU: " },
                new LabelElement { Type = "barcode", X = 20, Y = 130,
                                   BarcodeType = "Code128", BarcodeHeight = 24,
                                   Field = "Sku" },
                new LabelElement { Type = "rfid", X = 0, Y = 0,
                                   RfidBank = "EPC", RfidWords = 12 },
            }
        };
        if (!File.Exists(Path.Combine(_store.Root, starter.Id + ".json")))
            _store.Save(starter);
    }

    [RelayCommand]
    private void Reload()
    {
        Templates.Clear();
        foreach (var t in _store.LoadAll()) Templates.Add(t);
        Status = $"Loaded {Templates.Count} template(s).";
    }

    partial void OnSelectedTemplateChanged(LabelTemplate? value)
    {
        Elements.Clear();
        if (value != null)
            foreach (var el in value.Elements) Elements.Add(Clone(el));
        OnPropertyChanged(nameof(SelectedElement));
        UpdatePreview();
    }

    partial void OnSelectedElementChanged(LabelElement? value) => UpdatePreview();

    partial void OnSampleSkuChanged(string value) => UpdatePreview();
    partial void OnSampleNameChanged(string value) => UpdatePreview();
    partial void OnSampleSerialChanged(string value) => UpdatePreview();
    partial void OnSampleEpcChanged(string value) => UpdatePreview();
    partial void OnSampleQtyChanged(int value) => UpdatePreview();

    [RelayCommand]
    private void NewTemplate()
    {
        var t = new LabelTemplate
        {
            Name = "New Template",
            WidthMm = 73, HeightMm = 20, Dpi = 203,
        };
        Templates.Add(t);
        SelectedTemplate = t;
        Status = "Created new template — give it a name and add elements.";
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedTemplate == null) return;
        try
        {
            // Persist current Elements back into the template before writing
            SelectedTemplate.Elements.Clear();
            foreach (var el in Elements) SelectedTemplate.Elements.Add(el);
            _store.Save(SelectedTemplate);
            Status = $"Saved '{SelectedTemplate.Name}'.";
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = "Save failed: " + ex.Message;
            Status = "Save failed";
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedTemplate == null) return;
        var name = SelectedTemplate.Name;
        try
        {
            _store.Delete(SelectedTemplate);
            Templates.Remove(SelectedTemplate);
            SelectedTemplate = Templates.FirstOrDefault();
            Status = $"Deleted '{name}'.";
        }
        catch (Exception ex)
        {
            LastError = "Delete failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void AddText()    => AddElement(new LabelElement { Type = "text",    X = 20, Y = 20, Content = "New text",  FontHeight = 40, FontWidth = 40 });
    [RelayCommand]
    private void AddBarcode() => AddElement(new LabelElement { Type = "barcode", X = 20, Y = 20, Field = "Sku", BarcodeType = "Code128", BarcodeHeight = 80 });
    [RelayCommand]
    private void AddQrCode()  => AddElement(new LabelElement { Type = "qrcode",  X = 20, Y = 20, Field = "QrPayload", QrMagnification = 5 });
    [RelayCommand]
    private void AddRfid()    => AddElement(new LabelElement { Type = "rfid",    X = 0,  Y = 0,  RfidBank = "EPC", RfidWords = 12 });
    [RelayCommand]
    private void AddLine()    => AddElement(new LabelElement { Type = "line",    X = 20, Y = 20, Width = 200, Thickness = 2 });
    [RelayCommand]
    private void AddBox()     => AddElement(new LabelElement { Type = "box",     X = 20, Y = 20, Width = 200, Height = 100, Thickness = 2 });

    private void AddElement(LabelElement el)
    {
        Elements.Add(el);
        SelectedElement = el;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedElement == null) return;
        Elements.Remove(SelectedElement);
        SelectedElement = null;
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedElement == null) return;
        var i = Elements.IndexOf(SelectedElement);
        if (i > 0) Elements.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedElement == null) return;
        var i = Elements.IndexOf(SelectedElement);
        if (i >= 0 && i < Elements.Count - 1) Elements.Move(i, i + 1);
    }

    private void UpdatePreview()
    {
        if (SelectedTemplate == null) { LabelPreview = ""; return; }
        try
        {
            var spec = new LabelSpec(
                Sku: SampleSku, Name: SampleName, Qty: SampleQty,
                Serial: SampleSerial, Epc: SampleEpc,
                EncodeRfid: Elements.Any(e => e.Type == "rfid"));
            LabelPreview = PplzBuilder.BuildFromTemplate(SelectedTemplate, spec);
        }
        catch (Exception ex)
        {
            LabelPreview = "// preview error: " + ex.Message;
        }
    }

    private static LabelElement Clone(LabelElement src) => new()
    {
        Type = src.Type, X = src.X, Y = src.Y, Rotation = src.Rotation,
        Content = src.Content, Field = src.Field, Prefix = src.Prefix,
        Font = src.Font, FontHeight = src.FontHeight, FontWidth = src.FontWidth,
        BarcodeType = src.BarcodeType, BarcodeHeight = src.BarcodeHeight,
        QrMagnification = src.QrMagnification,
        Width = src.Width, Height = src.Height, Thickness = src.Thickness,
        RfidWords = src.RfidWords, RfidBank = src.RfidBank,
    };
}