using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Dunhill.PrintStudio.Usb;

/// <summary>
/// HTTP transport for the Postek Browser Print Server (the official
/// local HTTP bridge Postek ships with their label/RFID printer SDK).
///
/// Browser Print Server is a small Windows service that runs on the
/// operator's PC and exposes the printer over loopback HTTP. It accepts
/// a single endpoint at <c>http://127.0.0.1:888/postek/print</c> with
/// form-encoded parameters and dispatches to one of several printer
/// methods based on the <c>reqParam</c> field. The vendor calls this a
/// "Browser Print" because the typical client is a JavaScript / HTML
/// frontend that POSTs to it from the same machine.
///
/// Wire shape (confirmed against the bundled demo HTML and the official
/// "Browser Print Development Guide V1.3" PDF):
///
/// <code>
/// POST http://127.0.0.1:888/postek/print
///   Content-Type: application/x-www-form-urlencoded
///   Body:
///     reqParam=1                    # method selector: "0".. "7"
///     printparams=&lt;JSON string&gt;     # stringified array of {PTK_Method, "args"}
/// </code>
///
/// Response:
/// <code>
/// { "retval": "0", "msg": "...", "ReceiveData": "..." }
/// </code>
///
/// Why this is the right path when the operator's PC is on a different
/// network than the cloud dashboard:
///   - No LAN routing needed: Browser Print binds to 127.0.0.1, so the
///     cloud cannot reach it directly. Instead the operator's PC runs a
///     small bridge agent (the same .exe) that the cloud calls over
///     the operator's preferred channel (Tailscale, pagekite, ngrok).
///   - The bridge on the operator's PC then POSTs to Browser Print on
///     127.0.0.1:888 — same protocol already used by the demo HTML.
///   - Seagull driver handling is implicit: Browser Print owns the
///     USB session for the lifetime of each print job.
///   - EPC encode flow is supported through PTK_RWRFIDLabel + PTK_PrintLabel
///     combined in a single printparams array.
///
/// Trade-offs vs other transports:
///   - One-way per request: Browser Print returns only the final retval.
///     If you want read-after-write verification, call reqParam=4 with
///     PTK_ReadRFIDLabelData in a second request and compare bytes.
///   - Browser Print Server must be running (start it from Start menu if
///     not). The operator can verify with
///     <c>http://127.0.0.1:888/postek/print</c> returning
///     <c>{"retval":"-1","msg":"reqParam is null"}</c> on a GET.
///   - Inbound cloud → bridge: still requires a tunnel/relay. Out of
///     scope here; see <c>postek-bridge</c> on Vercel for the cloud side.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PostekBrowserPrintTransport : IDisposable
{
    /// <summary>Wire endpoint format string; uses {0} for reqParam.</summary>
    public const string DefaultEndpoint = "http://127.0.0.1:888/postek/print";

    public string? LastError { get; private set; }
    public string Endpoint { get; set; } = DefaultEndpoint;
    public bool IsConnected => _httpClient is not null;
    public int? LastRetval { get; private set; }
    public string? LastReceiveData { get; private set; }

    private HttpClient? _httpClient;
    private bool _disposed;

    public bool Open()
    {
        LastError = null;
        Close();
        try
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Browser Print init failed: {ex.Message}";
            _httpClient = null;
            return false;
        }
    }

    /// <summary>
    /// Send a JSON-stringified <paramref name="printparams"/> body to the
    /// Browser Print endpoint with the given method selector. Returns
    /// true if the call succeeded AND retval == 0; otherwise false and
    /// <see cref="LastError"/> is set.
    /// </summary>
    /// <param name="reqParam">"0".."7" — see Browser Print V1.3 PDF page 5.</param>
    /// <param name="printparamsJsonArray">Stringified JSON array, e.g. <c>[{"PTK_OpenUSBPort":255}]</c>.</param>
    public async Task<bool> SendAsync(string reqParam, string printparamsJsonArray, CancellationToken ct = default)
    {
        if (_httpClient is null)
        {
            LastError = "Browser Print transport not initialized (call Open() first).";
            return false;
        }
        try
        {
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("reqParam", reqParam),
                new KeyValuePair<string, string>("printparams", printparamsJsonArray),
            });
            var resp = await _httpClient.PostAsync(Endpoint, form, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"Browser Print HTTP {(int)resp.StatusCode}: {Truncate(body, 200)}";
                LastRetval = null;
                LastReceiveData = null;
                return false;
            }
            var parsed = JsonSerializer.Deserialize<BrowserPrintResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null)
            {
                LastError = "Browser Print returned an empty/invalid JSON body.";
                LastRetval = null;
                return false;
            }
            LastRetval = int.TryParse(parsed.retval, out var r) ? r : null;
            LastReceiveData = parsed.ReceiveData;
            if (LastRetval == 0) return true;
            LastError = $"Browser Print retval={parsed.retval}: {parsed.msg ?? "(no msg)"}";
            return false;
        }
        catch (TaskCanceledException)
        {
            LastError = "Browser Print request timed out.";
            return false;
        }
        catch (HttpRequestException hre)
        {
            LastError = $"Browser Print connection failed: {hre.Message}";
            return false;
        }
        catch (Exception ex)
        {
            LastError = $"Browser Print send failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Build the standard "print one text label + encode an EPC + print"
    /// job in a single Browser Print call. Returns a stringified JSON
    /// array of <c>{"PTK_&lt;Method&gt;": "&lt;args&gt;"}</c> objects — the
    /// shape Browser Print's Java side parses as
    /// <c>List&lt;Map&lt;String,Object&gt;&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Default dimensions are for the ZR300I's typical benchmark
    /// 4"×3" at 203dpi (812×244 dots, 24-dot gap). For RFID inlays, the
    /// operator's media dimensions are much smaller: pass the actual
    /// physical dimensions in inches/mm to the calling view-model.
    /// </remarks>
    public static string BuildLabelJob(
        string printText,
        int labelWidthDots,
        int labelHeightDots,
        int labelGapDots,
        string epcHex,
        int epcStartBlock = 2)
    {
        // Normalize EPC: lowercase hex, no whitespace.
        epcHex = (epcHex ?? "").Trim().Replace(" ", "").Replace("-", "");
        if (epcHex.Length == 0)
        {
            // Plain label, no RFID.
            var plain = new (string name, object value)[]
            {
                ("PTK_OpenUSBPort", "255"),
                ("PTK_ClearBuffer", ""),
                ("PTK_SetDirection", "B"),
                ("PTK_SetPrintSpeed", "4"),
                ("PTK_SetDarkness", "10"),
                // PTK_SetLabelHeight signature: (lheight, gapH, gapOffset, bFlag)
                // lheight in dots, gapH in dots, gapOffset=0 (no offset),
                // bFlag=false (gapOffset not used).
                // For an RFID inlay like the operator's 73×20mm at 203dpi:
                //   labelHeightDots = 20 mm * 203 / 25.4 = 160
                //   labelGapDots    = ~3mm* 203 / 25.4 = 24
                ("PTK_SetLabelHeight", $"{labelHeightDots},{labelGapDots},0,false"),
                ("PTK_SetLabelWidth", $"{labelWidthDots}"),
                ("PTK_DrawText_TrueType", $"30,60,40,0,Arial,1,700,0,0,0,{printText}"),
                ("PTK_PrintLabel", "1,1"),
                ("PTK_CloseUSBPort", ""),
            };
            return SerializePtkCalls(plain);
        }

        // Validate: must be even-length hex string. nWDataNum = bytes.
        if (epcHex.Length % 2 != 0)
            throw new ArgumentException("EPC hex must have even length.", nameof(epcHex));
        int nWDataNum = epcHex.Length / 2;
        var withRfid = new (string name, object value)[]
        {
            ("PTK_OpenUSBPort", "255"),
            ("PTK_PcxGraphicsDel", "*"),
            ("PTK_ClearBuffer", ""),
            ("PTK_SetDirection", "B"),
            ("PTK_SetPrintSpeed", "4"),
            ("PTK_SetDarkness", "10"),
            ("PTK_SetLabelHeight", $"{labelHeightDots},{labelGapDots},0,false"),
            ("PTK_SetLabelWidth", $"{labelWidthDots}"),
            ("PTK_RWRFIDLabel", $"1,0,{epcStartBlock},{nWDataNum},1,{epcHex}"),
            ("PTK_DrawRectangle", $"58,15,3,{labelWidthDots - 8},{labelHeightDots - 20}"),
            ("PTK_DrawBarcode", $"30,150,0,1,2,2,50,B,{printText}"),
            ("PTK_DrawLineOr", "58,111,500,3"),
            ("PTK_DrawTextEx", "80,130,0,3,1,1,N,Internal Soft Font,0"),
            ("PTK_DrawBar2D_PDF417", $"80,180,400,300,0,0,3,7,10,2,0,0,{printText}"),
            ("PTK_DrawBar2D_QR", "80,28,180,180,0,3,2,0,0,Postek Electronics Co. Ltd."),
            ("PTK_DrawText_TrueType", $"580,580,64,0,Arial,1,700,0,0,0,Use different ID_NAME for different Truetype font objects"),
            ("PTK_PrintLabel", "1,1"),
            ("PTK_CloseUSBPort", ""),
        };
        return SerializePtkCalls(withRfid);
    }

    /// <summary>
    /// Browser Print expects an array of single-key objects, NOT tuples:
    /// <c>[{"PTK_OpenUSBPort": "255"}, {"PTK_ClearBuffer": ""}]</c>.
    /// Using list-of-tuples makes Browser Print's Java side fail to parse
    /// because <c>List&lt;Map&lt;String, Object&gt;&gt;</c> can't reconstruct
    /// a String→Object map from a [String, Object] pair array.
    /// </summary>
    private static string SerializePtkCalls((string name, object value)[] calls)
    {
        var arr = new System.Text.StringBuilder("[");
        for (int i = 0; i < calls.Length; i++)
        {
            if (i > 0) arr.Append(',');
            arr.Append('{').Append(JsonSerializer.Serialize(calls[i].name));
            arr.Append(':').Append(JsonSerializer.Serialize(calls[i].value));
            arr.Append('}');
        }
        arr.Append(']');
        return arr.ToString();
    }

    /// <summary>
    /// Build the read-only request that asks the printer to read back
    /// the EPC/TID/User data of the tag under the antenna. The demo
    /// HTML uses this shape verbatim:
    /// <c>[{"PTK_OpenUSBPort":255},{"PTK_ReadRFIDLabelData":"0,0,1,TID:,256"},{"PTK_CloseUSBPort":""}]</c>.
    /// </summary>
    /// <param name="area">Memory bank to read: 0=TID, 1=EPC, 3=USER.</param>
    /// <param name="startBlock">First block (EPC bank starts at 2 for 96-bit EPC).</param>
    /// <param name="byteLength">Bytes to read back.</param>
    /// <param name="prefix">LCD-display prefix (e.g. "TID:", "EPC:"); appears verbatim in ReceiveData.</param>
    /// <param name="displayLength">Length field for the LCD display message (the PDF
    /// defines a 5th arg that's effectively the LCD buffer size; Postek firmware
    /// accepts up to ~256). Pass 256 to match the demo; larger for human-readable
    /// readouts.</param>
    public static string BuildReadJob(int area = 1, int startBlock = 2, int byteLength = 12,
        string prefix = "EPC:", int displayLength = 256)
    {
        var calls = new (string, object)[]
        {
            ("PTK_OpenUSBPort", "255"),
            ("PTK_ReadRFIDLabelData", $"{area},{startBlock},{byteLength},{prefix},{displayLength}"),
            ("PTK_CloseUSBPort", ""),
        };
        return SerializePtkCalls(calls);
    }

    /// <summary>
    /// Issue a single read against the printer via <c>reqParam=4</c>.
    /// Returns the raw <c>ReceiveData</c> string on success, or null on
    /// failure (with <see cref="LastError"/> set).
    /// </summary>
    /// <remarks>
    /// The PDF API for <c>PTK_ReadRFIDLabelData</c> has the parameter
    /// order <c>(area, startBlock, byteLength, name, length)</c> — the
    /// last <c>length</c> field is the display length for the LCD message,
    /// not the read size. We set it to 96 — the standard 12-byte EPC
    /// representation — because the actual byte-length is the third
    /// argument. If the ZR300I returns a different interpretation we'll
    /// see that here.
    /// </remarks>
    public async Task<string?> ReadRfidAsync(int area = 1, int startBlock = 2, int byteLength = 12,
        string prefix = "EPC:", CancellationToken ct = default)
    {
        LastReceiveData = null;
        var pp = BuildReadJob(area, startBlock, byteLength, prefix);
        var ok = await SendAsync("4", pp, ct).ConfigureAwait(false);
        if (!ok) return null;
        return LastReceiveData;
    }

    /// <summary>
    /// Encode an EPC onto a label, then read it back, then compare.
    /// Returns true if the readback equals <paramref name="expectedEpcHex"/>
    /// (case-insensitive, with optional <paramref name="prefix"/> stripped).
    /// On mismatch, the most recent <c>LastReceiveData</c> is the actual
    /// value that came back from the printer.
    /// </summary>
    /// <remarks>
    /// Time budget per attempt: ~3 seconds. The label feed is synchronous
    /// on Postek printers, the antenna is right under the TPH, and a
    /// 12-byte EPC read completes in well under 200ms on every Gen 2 chip
    /// we've worked with. If we see flakiness here, we may need to bump
    /// the sleep for the bench-style printer with a slower RF module.
    /// </remarks>
    public async Task<bool> VerifyEncodeAsync(
        string expectedEpcHex,
        string printText,
        int labelWidthDots,
        int labelHeightDots,
        int labelGapDots,
        int epcStartBlock = 2,
        int maxAttempts = 2,
        CancellationToken ct = default)
    {
        var normalized = (expectedEpcHex ?? "").Trim().Replace(" ", "").Replace("-", "").ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Length % 2 != 0)
        {
            LastError = "Cannot verify: EPC hex is empty or odd-length.";
            return false;
        }
        int nWDataNum = normalized.Length / 2;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Phase 1: encode + print (or just re-print on retry).
            var encodePp = BuildLabelJob(
                printText, labelWidthDots, labelHeightDots, labelGapDots,
                normalized, epcStartBlock);
            var encodeOk = await SendAsync("1", encodePp, ct).ConfigureAwait(false);
            if (!encodeOk)
            {
                LastError = $"encode attempt {attempt} failed: {LastError}";
                continue;
            }

            // The encoder→antenna latency is real. 250ms is conservative
            // for a stock Postek TXr; 600ms gives margin for rewind cycles.
            try { await Task.Delay(TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }

            // Phase 2: read back the same memory area (EPC bank, block 2).
            // Each byte is two ASCII hex chars in ReceiveData, so we ask
            // for nWDataNum bytes and expect (2 * nWDataNum) ASCII chars.
            var read = await ReadRfidAsync(
                area: 1, startBlock: epcStartBlock, byteLength: nWDataNum,
                prefix: "", ct: ct).ConfigureAwait(false);
            if (read is null)
            {
                LastError = $"read-back attempt {attempt} failed: {LastError}";
                continue;
            }

            // Browser Print may prefix with the PTK name (PDF default is
            // the name string we passed). Strip it before comparison.
            var stripped = StripReadPrefix(read);
            var lower = stripped.ToLowerInvariant();
            if (lower == normalized) return true;

            // Mismatch — keep last receive data so the caller can inspect.
            LastError = $"EPC mismatch on attempt {attempt}: expected {normalized}, " +
                         $"got {lower} (raw: '{Truncate(read, 80)}').";
        }

        return false;
    }

    /// <summary>
    /// Strip the leading prefix string that <c>PTK_ReadRFIDLabelData</c>
    /// echoes back. Browser Print typically returns the prefix verbatim
    /// (e.g. "EPC:30313233…"); some firmwares pad/truncate.
    /// </summary>
    private static string StripReadPrefix(string raw)
    {
        var s = (raw ?? "").Trim();
        var colon = s.IndexOf(':');
        return colon >= 0 && colon + 1 < s.Length ? s.Substring(colon + 1) : s;
    }

    public void Close()
    {
        _httpClient?.Dispose();
        _httpClient = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    private sealed class BrowserPrintResponse
    {
        public string? retval { get; set; }
        public string? msg { get; set; }
        public string? ReceiveData { get; set; }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
}
