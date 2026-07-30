using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Dunhill.PrintStudio.Models;

/// <summary>
/// A reusable label layout. Stored as JSON in
/// %LOCALAPPDATA%\DunhillPrintStudio\templates\&lt;name&gt;.json
/// and round-tripped through <see cref="TemplateStore"/>.
///
/// A template is a list of <see cref="LabelElement"/>s positioned in
/// printer-dot coordinates on a label of <see cref="WidthMm"/> × <see cref="HeightMm"/>
/// at <see cref="Dpi"/> DPI. <see cref="PplzBuilder.BuildFromTemplate"/> renders
/// the template into PPLZ for a given <see cref="LabelSpec"/>.
/// </summary>
public sealed class LabelTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int WidthMm { get; set; }
    public int HeightMm { get; set; }
    public int Dpi { get; set; } = 203;
    public byte Darkness { get; set; } = 8;
    public byte PrintSpeed { get; set; } = 4;
    public int GapDots { get; set; } = 24;
    public List<LabelElement> Elements { get; set; } = new();

    /// <summary>Convenience: physical label size in printer dots at this template's DPI.</summary>
    [JsonIgnore]
    public int WidthDots => (int)(WidthMm * Dpi / 25.4);
    [JsonIgnore]
    public int HeightDots => (int)(HeightMm * Dpi / 25.4);
}

/// <summary>
/// One drawable thing on the label. Discriminated by <see cref="Type"/>.
/// Position is in printer dots from the label origin (top-left).
/// </summary>
public sealed class LabelElement
{
    /// <summary>"text" | "barcode" | "qrcode" | "rfid" | "line" | "box"</summary>
    public string Type { get; set; } = "text";
    public int X { get; set; }
    public int Y { get; set; }
    public int Rotation { get; set; } = 0;

    // ----- Text / Barcode / QRCode -----
    /// <summary>For Text: literal text (no binding). For Barcode/QRCode: field name on LabelSpec, or literal.</summary>
    public string? Content { get; set; }
    /// <summary>For Text: bind to LabelSpec field. Takes precedence over Content if set. e.g. "Sku", "Name", "Serial", "Epc".</summary>
    public string? Field { get; set; }
    /// <summary>For Text: prefix string prepended to the rendered value (e.g. "SKU: ").</summary>
    public string? Prefix { get; set; }
    /// <summary>For Text: ZPL font letter (0..9, A..Z). 0 = default scalable.</summary>
    public string Font { get; set; } = "0";
    /// <summary>For Text: character height in dots.</summary>
    public int FontHeight { get; set; } = 40;
    /// <summary>For Text: character width in dots.</summary>
    public int FontWidth { get; set; } = 40;

    // ----- Barcode / QRCode -----
    /// <summary>Barcode symbology: "Code128", "Code39", "EAN13", "UPCA". Ignored for QRCode.</summary>
    public string BarcodeType { get; set; } = "Code128";
    /// <summary>Barcode bar height in dots. Ignored for QRCode.</summary>
    public int BarcodeHeight { get; set; } = 80;
    /// <summary>QR code magnification. Ignored for Barcode.</summary>
    public int QrMagnification { get; set; } = 5;

    // ----- Line / Box -----
    public int Width { get; set; }    // line length OR box width in dots
    public int Height { get; set; }   // box height in dots (0 for lines)
    public int Thickness { get; set; } = 2;

    // ----- RFID -----
    /// <summary>EPC bank word count (12 = 96-bit EPC, 16 = 128-bit).</summary>
    public int RfidWords { get; set; } = 12;
    /// <summary>"EPC" | "TID" | "USER". Defaults to EPC.</summary>
    public string RfidBank { get; set; } = "EPC";

    // Display helpers for the Properties panel
    [JsonIgnore]
    public string DisplaySummary => Type switch
    {
        "text"    => $"Text: {(string.IsNullOrEmpty(Field) ? (Content ?? "") : $"{{{Field}}}")}",
        "barcode" => $"Barcode ({BarcodeType}): {(string.IsNullOrEmpty(Field) ? (Content ?? "") : $"{{{Field}}}")}",
        "qrcode"  => $"QR: {(string.IsNullOrEmpty(Field) ? (Content ?? "") : $"{{{Field}}}")}",
        "rfid"    => $"RFID encode ({RfidBank} {RfidWords}w)",
        "line"    => $"Line {Width}×{Thickness}",
        "box"     => $"Box {Width}×{Height}×{Thickness}",
        _         => Type
    };
}