using System.Text;
using Dunhill.PrintStudio.Models;

namespace Dunhill.PrintStudio.Pplz;

/// <summary>
/// PPLZ (ZPL-compatible) label command builder.
///
/// Postek ZR300I default language is PPLZ. Commands are ASCII bytes terminated
/// by newlines; the printer executes everything between ^XA and ^XZ as one
/// label format.
///
/// Reference: Postek ZR300I Programming Manual, chapter 4 (PPLZ command set).
/// Cross-reference: Zebra ZPL II programming guide (PPLZ is a strict superset
/// of the subset Zebra documents).
/// </summary>
public static class PplzBuilder
{
    // PPLZ control characters
    public const string STX = "\u0002";   // ^X start of label
    public const string ETX = "\u0003";   // ^X end of label (we use ^XZ)
    public const string CRLF = "\r\n";

    /// <summary>
    /// Build a complete PPLZ label format string for a single inventory item.
    /// Width/height in printer dots (203 dpi for ZR300I default).
    /// </summary>
    public static string BuildItemLabel(LabelSpec spec, LabelDimensions dims)
    {
        var sb = new System.Text.StringBuilder(1024);

        // ^XA — start of label
        sb.Append("^XA");

        // ^CI28 — UTF-8 encoding for international text
        sb.Append("^CI28");

        // ^PW — print width
        sb.Append("^PW").Append(dims.WidthDots);

        // ^LL — label length (height)
        sb.Append("^LL").Append(dims.HeightDots);

        // ^LH0,0 — label home (origin)
        sb.Append("^LH0,0");

        // ^MD{n} — media darkness (0-30, default 8 for ZR300I)
        sb.Append("^MD").Append(spec.Darkness);

        // ^PRn — print speed (2-6 inches/sec for ZR300I)
        sb.Append("^PR").Append(spec.PrintSpeed);

        // --- Header bar with company name ---
        sb.Append("^FO").Append(20).Append(',').Append(20);
        sb.Append("^A0N,40,40");
        sb.Append("^FD").Append(Escape("DUNHILL-GLOBAL")).Append("^FS");

        // --- SKU title (large) ---
        sb.Append("^FO").Append(20).Append(',').Append(80);
        sb.Append("^A0N,72,72");
        sb.Append("^FD").Append(Escape($"SKU: {spec.Sku}")).Append("^FS");

        // --- Item name (medium) ---
        if (!string.IsNullOrEmpty(spec.Name))
        {
            sb.Append("^FO").Append(20).Append(',').Append(170);
            sb.Append("^A0N,40,40");
            // Truncate to ~30 chars to fit 812-dot width
            var name = spec.Name.Length > 30 ? spec.Name[..30] : spec.Name;
            sb.Append("^FD").Append(Escape(name)).Append("^FS");
        }

        // --- Code 128 barcode ---
        sb.Append("^FO").Append(20).Append(',').Append(230);
        sb.Append("^BY3,2,80");  // bar width 3, narrow-to-wide 2:1, height 80
        sb.Append("^BCN,80,Y,N,N");  // Code 128, height 80, print interpret line, no UCC, no mode
        sb.Append("^FD").Append(Escape(spec.Sku)).Append("^FS");

        // --- QR code (if URL or asset id provided) ---
        if (!string.IsNullOrEmpty(spec.QrPayload))
        {
            sb.Append("^FO").Append(580).Append(',').Append(230);
            sb.Append("^BQN,2,5");  // QR, model 2, magnification 5
            sb.Append("^FDLA,").Append(Escape(spec.QrPayload)).Append("^FS");
        }

        // --- Footer: serial / date / qty ---
        var yFooter = dims.HeightDots - 100;
        sb.Append("^FO").Append(20).Append(',').Append(yFooter);
        sb.Append("^A0N,28,28");
        var footer = $"Qty: {spec.Qty}  |  Printed: {DateTime.Now:yyyy-MM-dd HH:mm}";
        if (!string.IsNullOrEmpty(spec.Serial))
            footer = $"S/N: {spec.Serial}  |  {footer}";
        sb.Append("^FD").Append(Escape(footer)).Append("^FS");

        // --- RFID encode command (only for ZR300I-RFID variant) ---
        if (spec.EncodeRfid && !string.IsNullOrEmpty(spec.Epc))
        {
            // ^RFW — read/write RFID; H = HF (we use UHF Gen2 for ZR300I);
            // 0 = EPC bank; 12 = number of words (96-bit EPC = 12 words of 16 bits)
            // E = error correction; format = EPC
            sb.Append("^RFW,U,2,12,E").Append(CRLF);
            sb.Append("^FD").Append(Escape(spec.Epc)).Append("^FS");
        }

        // ^XZ — end of label
        sb.Append("^XZ");

        return sb.ToString();
    }

    /// <summary>
    /// Build a "test" label — just a box with text. Useful for printer health check.
    /// </summary>
    public static string BuildTestLabel(LabelDimensions dims)
    {
        return
            "^XA" +
            $"^PW{dims.WidthDots}^LL{dims.HeightDots}^LH0,0" +
            "^FO100,100^A0N,80,80^FDPRINTER OK^FS" +
            "^FO100,220^A0N,40,40^FD" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "^FS" +
            "^FO100,300^BY3,2,80^BCN,80,Y,N,N^FDTEST-LABEL^FS" +
            "^XZ";
    }

    /// <summary>
    /// Build a ZPL status request — printer responds with status bytes.
    /// Use ~HS to query host status (print head, ribbon, paper).
    /// </summary>
    public static string BuildStatusRequest() => "~HS";

    /// <summary>
    /// Render a saved <see cref="LabelTemplate"/> into a PPLZ label for a given
    /// <see cref="LabelSpec"/>. Each template element becomes the matching PPLZ
    /// command sequence (text ^A0N+^FD, barcode ^BC, QR ^BQ, line/box ^GB,
    /// RFID ^RFW). Field-bound elements pull their value from <paramref name="spec"/>.
    /// </summary>
    public static string BuildFromTemplate(LabelTemplate template, LabelSpec spec)
    {
        var sb = new StringBuilder(1024);

        // ^XA — start of label
        sb.Append("^XA");
        sb.Append("^CI28");                     // UTF-8 encoding for international text
        sb.Append("^PW").Append(template.WidthDots);
        sb.Append("^LL").Append(template.HeightDots);
        sb.Append("^LH0,0");
        sb.Append("^MD").Append(template.Darkness);
        sb.Append("^PR").Append(template.PrintSpeed);

        foreach (var el in template.Elements)
        {
            switch (el.Type)
            {
                case "text":
                    AppendText(sb, el, ResolveValue(el, spec));
                    break;
                case "barcode":
                    AppendBarcode(sb, el, ResolveValue(el, spec));
                    break;
                case "qrcode":
                    AppendQrCode(sb, el, ResolveValue(el, spec));
                    break;
                case "rfid":
                    AppendRfid(sb, el, spec);
                    break;
                case "line":
                    AppendLine(sb, el);
                    break;
                case "box":
                    AppendBox(sb, el);
                    break;
            }
        }

        sb.Append("^XZ");
        return sb.ToString();
    }

    /// <summary>
    /// Resolve the rendered string for a text/barcode/QR element. Honors the
    /// <see cref="LabelElement.Field"/> binding first (pulls from
    /// <see cref="LabelSpec"/>), then falls back to <see cref="LabelElement.Content"/>.
    /// </summary>
    private static string ResolveValue(LabelElement el, LabelSpec spec)
    {
        string raw = el.Field switch
        {
            "Sku"      => spec.Sku,
            "Name"     => spec.Name,
            "Serial"   => spec.Serial ?? "",
            "Epc"      => spec.Epc ?? "",
            "Qty"      => spec.Qty.ToString(),
            "QrPayload"=> spec.QrPayload ?? "",
            null or "" => el.Content ?? "",
            _          => el.Content ?? el.Field,   // unknown field → render literal
        };
        if (string.IsNullOrEmpty(el.Prefix)) return raw;
        return el.Prefix + raw;
    }

    private static void AppendText(StringBuilder sb, LabelElement el, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append("^FO").Append(el.X).Append(',').Append(el.Y);
        sb.Append("^A").Append(el.Font).Append('N')
          .Append(',').Append(el.FontHeight).Append(',').Append(el.FontWidth);
        sb.Append("^FD").Append(Escape(value)).Append("^FS");
    }

    private static void AppendBarcode(StringBuilder sb, LabelElement el, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append("^FO").Append(el.X).Append(',').Append(el.Y);
        sb.Append("^BY3,2,").Append(el.BarcodeHeight);   // narrow-wide ratio + bar height
        var symb = el.BarcodeType.ToUpperInvariant() switch
        {
            "CODE39" => "B3N",
            "EAN13"  => "BEN",
            "UPCA"   => "BAN",
            _        => "BCN",                              // Code 128 default
        };
        // ^BC<dir>,<height>,<print-interpret-line>,<UCC-check>,<mode>
        sb.Append('^').Append(symb).Append('N')
          .Append(',').Append(el.BarcodeHeight)
          .Append(",Y,N,N");
        sb.Append("^FD").Append(Escape(value)).Append("^FS");
    }

    private static void AppendQrCode(StringBuilder sb, LabelElement el, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append("^FO").Append(el.X).Append(',').Append(el.Y);
        sb.Append("^BQN,2,").Append(el.QrMagnification);   // model 2, mag = ...
        sb.Append("^FDLA,").Append(Escape(value)).Append("^FS");
    }

    private static void AppendLine(StringBuilder sb, LabelElement el)
    {
        // ^GB<width>,<height>,<thickness>,<color>  — height=1, thickness=N → horizontal line
        sb.Append("^FO").Append(el.X).Append(',').Append(el.Y);
        sb.Append("^GB").Append(el.Width).Append(",1,").Append(el.Thickness).Append(",B^FS");
    }

    private static void AppendBox(StringBuilder sb, LabelElement el)
    {
        sb.Append("^FO").Append(el.X).Append(',').Append(el.Y);
        sb.Append("^GB").Append(el.Width).Append(',').Append(el.Height)
          .Append(',').Append(el.Thickness).Append(",B^FS");
    }

    private static void AppendRfid(StringBuilder sb, LabelElement el, LabelSpec spec)
    {
        // Only emits the encode command if the spec actually has an EPC. The
        // user can have an RFID element in the template but skip encoding for
        // a particular print job (e.g. a re-print of an already-encoded label).
        if (string.IsNullOrEmpty(spec.Epc)) return;
        // ^RFW,<frequency>,<lock>,<words>,<format>
        //   frequency = U (UHF Gen2)
        //   lock      = 2 (lock none — leave tag unlocked for further writes)
        //   words     = number of 16-bit words to write
        //   format    = E (EPC bank) / H (handle) / etc.
        sb.Append("^RFW,U,2,").Append(el.RfidWords).Append(',').Append(el.RfidBank[0]).Append(CRLF);
        sb.Append("^FD").Append(Escape(spec.Epc)).Append("^FS");
    }

    /// <summary>
    /// Escape PPLZ special characters in field data:
    ///   ^  →   caret (PPLZ field separator)
    ///   ~  →   tilde (PPLZ format command prefix)
    ///   \  →   backslash (PPLZ escape)
    /// </summary>
    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s
            .Replace("\\", "\\\\")
            .Replace("^", "\\5E")
            .Replace("~", "\\7E");
    }
}

public sealed record LabelSpec(
    string Sku,
    string Name,
    int Qty = 1,
    string? Serial = null,
    string? QrPayload = null,
    bool EncodeRfid = false,
    string? Epc = null,
    byte Darkness = 8,
    byte PrintSpeed = 4
);

public sealed record LabelDimensions(int WidthDots, int HeightDots, int GapDots = 24);
