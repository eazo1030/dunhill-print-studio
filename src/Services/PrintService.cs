using Dunhill.PrintStudio.Models;
using Dunhill.PrintStudio.Pplz;
using Dunhill.PrintStudio.Usb;

namespace Dunhill.PrintStudio.Services;

/// <summary>
/// Owns the active printer connection and exposes a single
/// <see cref="PrintAsync"/> entry point. UI calls this; UI never touches
/// the transport directly.
///
/// Four transports:
///   - <b>Spooler</b>: the Seagull/Postek driver ships as a Windows print
///     queue. Our app opens a printer handle, sends raw PPLZ bytes
///     through <c>WritePrinter</c> with the RAW pass-through data type,
///     and Windows spooler + the vendor driver convert that to USB bulk
///     on the printer side. Same mechanism BarTender itself uses.
///   - <b>TCP :9100</b>: when the printer is on Ethernet. Bidirectional
///     read-back gives the EPC encode-result feedback (void-and-retry).
///   - <b>WinUSB</b>: experimental. If the operator's vendor driver is
///     replaced with WinUSB, raw bulk writes go straight to the
///     endpoints. Limited coverage on a ZR300I — try spooler first.
///   - <b>Browser Print</b>: Postek's local HTTP bridge running on the
///     operator's PC. POSTs to <c>http://127.0.0.1:888/postek/print</c>
///     with a JSON-stringified printparams array. Works for cases where
///     the cloud cannot reach the operator's PC directly (it just needs
///     the operator-side bridge to forward the request).
/// </summary>
public sealed class PrintService : IDisposable
{
    private readonly object _lock = new();
    private PostekTcpTransport? _tcp;
    private PostekUsbTransport? _usb;
    private PostekSpoolerTransport? _spooler;
    private PostekBrowserPrintTransport? _browserPrint;

    public PrinterStatus Status { get; private set; } = new(
        false, null, null, "Not connected", DateTime.UtcNow);

    public event EventHandler<PrinterStatus>? StatusChanged;

    public string? LastError { get; private set; }

    private void RaiseStatus() => StatusChanged?.Invoke(this, Status);

    public bool ConnectTcp(string host, int port = 9100)
    {
        lock (_lock)
        {
            Disconnect();
            _tcp = new PostekTcpTransport();
            if (!_tcp.Open(host, port))
            {
                LastError = _tcp.LastError;
                Status = Status with { Online = false, LastError = LastError };
                _tcp.Dispose();
                _tcp = null;
                RaiseStatus();
                return false;
            }
            Status = new PrinterStatus(
                Online: true,
                Model: "ZR300I (network)",
                ConnectionType: $"TCP {host}:{port}",
                LastError: null,
                LastChecked: DateTime.UtcNow);
            RaiseStatus();
            return true;
        }
    }

    /// <summary>
    /// Open the named Windows print queue for raw PPLZ. This is the path
    /// BarTender detects, and it's the easiest USB-connect story because
    /// the operator only has to install the printer once via the Seagull
    /// Driver Wizard (a one-time per-PC setup step).
    /// </summary>
    public bool ConnectSpooler(string printerName)
    {
        lock (_lock)
        {
            Disconnect();
            _spooler = new PostekSpoolerTransport();
            if (!_spooler.Open(printerName))
            {
                LastError = _spooler.LastError;
                Status = Status with { Online = false, LastError = LastError };
                _spooler.Dispose();
                _spooler = null;
                RaiseStatus();
                return false;
            }
            Status = new PrinterStatus(
                Online: true,
                Model: "ZR300I (Windows spooler)",
                ConnectionType: $"Spooler: {printerName}",
                LastError: null,
                LastChecked: DateTime.UtcNow);
            RaiseStatus();
            return true;
        }
    }

    /// <summary>Open the first WinUSB-class device that looks like a Postek.</summary>
    public bool ConnectUsb(ushort[]? acceptProductIds = null)
        => ConnectUsbInternal(pids: acceptProductIds, devicePath: null);

    public bool ConnectUsbPath(string devicePath)
        => ConnectUsbInternal(pids: null, devicePath: devicePath);

    private bool ConnectUsbInternal(ushort[]? pids, string? devicePath)
    {
        lock (_lock)
        {
            Disconnect();
            _usb = new PostekUsbTransport();
            var ok = devicePath is not null
                ? _usb.OpenDevice(devicePath)
                : _usb.Open(pids);
            if (!ok)
            {
                LastError = _usb.LastError;
                Status = Status with { Online = false, LastError = LastError };
                _usb.Dispose();
                _usb = null;
                RaiseStatus();
                return false;
            }
            var pid = _usb.DetectedProductId != 0 ? $" PID 0x{_usb.DetectedProductId:X4}" : "";
            Status = new PrinterStatus(
                Online: true,
                Model: "ZR300I (USB)",
                ConnectionType: $"USB{pid}",
                LastError: null,
                LastChecked: DateTime.UtcNow);
            RaiseStatus();
            return true;
        }
    }

    public void Disconnect()
    {
        _tcp?.Dispose();
        _usb?.Dispose();
        _spooler?.Dispose();
        _browserPrint?.Dispose();
        _tcp = null;
        _usb = null;
        _spooler = null;
        _browserPrint = null;
        Status = Status with { Online = false, ConnectionType = null };
        RaiseStatus();
    }

    /// <summary>
    /// Connect to Postek Browser Print Server on the operator's PC.
    /// Default endpoint is <c>http://127.0.0.1:888/postek/print</c>. The
    /// server is the local HTTP bridge Postek ships in their SDK.
    /// </summary>
    public bool ConnectBrowserPrint(string endpointUrl = PostekBrowserPrintTransport.DefaultEndpoint)
    {
        lock (_lock)
        {
            Disconnect();
            _browserPrint = new PostekBrowserPrintTransport { Endpoint = endpointUrl };
            if (!_browserPrint.Open())
            {
                LastError = _browserPrint.LastError;
                Status = Status with { Online = false, LastError = LastError };
                _browserPrint.Dispose();
                _browserPrint = null;
                RaiseStatus();
                return false;
            }
            Status = new PrinterStatus(
                Online: true,
                Model: "ZR300I (Browser Print)",
                ConnectionType: $"Browser Print {endpointUrl}",
                LastError: null,
                LastChecked: DateTime.UtcNow);
            RaiseStatus();
            return true;
        }
    }

    /// <summary>
    /// Confirm Browser Print Server is reachable by sending a list-printers
    /// probe (reqParam="0"). Returns true on any HTTP response with
    /// parseable JSON. Used by the Settings → Test connection button.
    /// </summary>
    public async Task<bool> BrowserPrintProbeAsync(CancellationToken ct = default)
    {
        if (_browserPrint is null || !_browserPrint.IsConnected) return false;
        // reqParam=0 returns the printer list. The actual response shape
        // across Postek Browser Print Server versions varies; we accept
        // any parseable JSON.
        return await _browserPrint.SendAsync("0", "[]", ct).ConfigureAwait(false);
    }

    public async Task<bool> PrintAsync(LabelSpec spec, LabelDimensions dims, CancellationToken ct = default)
    {
        var pplz = PplzBuilder.BuildItemLabel(spec, dims);
        return await SendRawAsync(pplz, ct);
    }

    public async Task<bool> PrintTestLabelAsync(LabelDimensions dims, CancellationToken ct = default)
    {
        var pplz = PplzBuilder.BuildTestLabel(dims);
        return await SendRawAsync(pplz, ct);
    }

    public async Task<byte[]?> ReadStatusAsync(int maxBytes = 64, CancellationToken ct = default)
    {
        PostekTcpTransport? tcp; PostekUsbTransport? usb; PostekSpoolerTransport? spooler;
        lock (_lock) { tcp = _tcp; usb = _usb; spooler = _spooler; }
        try
        {
            if (tcp is not null && tcp.IsConnected)    return await tcp.ReadStatusAsync(maxBytes, ct).ConfigureAwait(false);
            if (usb is not null && usb.IsConnected)    return await usb.ReadStatusAsync(maxBytes, ct).ConfigureAwait(false);
            // spooler path is one-way; returns null
        }
        catch (ObjectDisposedException) { }
        return null;
    }

    private async Task<bool> SendRawAsync(string pplz, CancellationToken ct)
    {
        // Snapshot active transports under the lock, then await OUTSIDE it.
        PostekTcpTransport? tcp; PostekUsbTransport? usb; PostekSpoolerTransport? spooler;
        PostekBrowserPrintTransport? browserPrint;
        lock (_lock)
        {
            tcp = _tcp; usb = _usb; spooler = _spooler; browserPrint = _browserPrint;
        }

        try
        {
            if (tcp is not null && tcp.IsConnected)
                return await tcp.SendAsync(pplz, ct).ConfigureAwait(false);
            if (spooler is not null && spooler.IsConnected)
                return await spooler.SendAsync(pplz, ct).ConfigureAwait(false);
            if (usb is not null && usb.IsConnected)
                return await usb.SendAsync(pplz, ct).ConfigureAwait(false);
            // Browser Print model is HTTP+JSON, not raw PPLZ bytes; the higher-level
            // PrintAsync() builds printparams and dispatches through this path. For
            // callers that only give us a PPLZ string, we wrap it as a single
            // PTK_DrawText_TrueType-style probe — but in practice Browser Print
            // callers should use the LabelSpec path.
            if (browserPrint is not null && browserPrint.IsConnected)
            {
                var wrapped = PostekBrowserPrintTransport.BuildLabelJob(
                    pplz ?? "(empty)", 800, 600, 24, epcHex: "");
                return await browserPrint.SendAsync("1", wrapped, ct).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            LastError = "Printer disconnected during print.";
            return false;
        }

        LastError = "Printer not connected. Click Connect in Settings.";
        return false;
    }

    /// <summary>
    /// High-level print path that carries a LabelSpec and RFID EPC.
    /// Routes through Browser Print if that's the active transport
    /// (PPLZ builders + EPC encoding happen here, since Browser Print
    /// expects <c>{PTK_*}</c> calls, not raw PPLZ bytes). When an EPC is
    /// supplied, runs a verify-encode-readback loop and emits the
    /// appropriate <see cref="PrintJobStatus"/> result.
    /// </summary>
    public async Task<PrintJobOutcome> PrintLabelJobAsync(LabelSpec spec, LabelDimensions dims, string? epcHex, CancellationToken ct = default)
    {
        // Try the active transport in priority order; settle on the first match.
        PostekBrowserPrintTransport? bp;
        PostekTcpTransport? tcp;
        PostekSpoolerTransport? spooler;
        PostekUsbTransport? usb;
        lock (_lock)
        {
            bp = _browserPrint; tcp = _tcp; spooler = _spooler; usb = _usb;
        }

        var epc = (epcHex ?? "").Trim();
        var text = (spec?.Sku ?? "") + (string.IsNullOrEmpty(spec?.Name) ? "" : "  " + spec.Name);

        // Browser Print path — has full verify-on-print support.
        if (bp is not null && bp.IsConnected)
        {
            if (epc.Length == 0)
            {
                // Plain label, no EPC. Encode path issues one Browser Print
                // call; we have no read-back to verify against.
                var pp = PostekBrowserPrintTransport.BuildLabelJob(
                    text, dims.WidthDots, dims.HeightDots,
                    Math.Max(0, dims.HeightDots / 25), epcHex: "");
                var ok = await bp.SendAsync("1", pp, ct).ConfigureAwait(false);
                return new PrintJobOutcome(
                    ok ? PrintJobStatus.Done : PrintJobStatus.Failed,
                    bp.LastReceiveData,
                    bp.LastError);
            }

            // EPC path: encode+readback verify loop, up to 2 attempts.
            var verified = await bp.VerifyEncodeAsync(
                expectedEpcHex: epc,
                printText: text,
                labelWidthDots: dims.WidthDots,
                labelHeightDots: dims.HeightDots,
                labelGapDots: Math.Max(0, dims.HeightDots / 25),
                epcStartBlock: 2,
                maxAttempts: 2,
                ct: ct).ConfigureAwait(false);

            return verified
                ? new PrintJobOutcome(PrintJobStatus.Done, bp.LastReceiveData, null)
                : new PrintJobOutcome(PrintJobStatus.VoidLabel, bp.LastReceiveData, bp.LastError);
        }

        // Non-Browser-Print transports don't have access to a verify loop
        // without the Browser Print Server, so they degrade to one-shot
        // print. Caller is responsible for downstream verification if
        // they care about it (e.g. via a UHF reader plugged elsewhere).
        var pplz = PplzBuilder.BuildItemLabel(spec ?? new LabelSpec(string.Empty), dims);
        var sent = await SendRawAsync(pplz, ct).ConfigureAwait(false);
        return new PrintJobOutcome(
            sent ? PrintJobStatus.Done : PrintJobStatus.Failed,
            null,
            LastError);
    }

    public void Dispose() => Disconnect();
}
