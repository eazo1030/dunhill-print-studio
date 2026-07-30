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
