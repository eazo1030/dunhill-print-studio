using Dunhill.PrintStudio.Models;
using Dunhill.PrintStudio.Pplz;
using Dunhill.PrintStudio.Usb;

namespace Dunhill.PrintStudio.Services;

/// <summary>
/// Owns the active printer connection (TCP, WinUSB, or Windows print
/// spooler) and exposes a single <see cref="PrintAsync"/> entry point. UI
/// calls this; UI never touches the transport directly.
///
/// Three transports, three modes:
///   - <b>Spooler</b> (recommended for USB-attached Postek ZR300I): the
///     Seagull/Postek driver ships as a Windows print queue. Our app opens
///     a printer handle, sends raw PPLZ bytes through <c>WritePrinter</c>
///     with the RAW pass-through data type, and Windows spooler + the
///     vendor driver convert that to USB bulk on the printer side. Same
///     mechanism BarTender itself uses. No Zadig, no driver swap.
///   - <b>TCP :9100</b>: when the printer is on Ethernet. Bidirectional
///     read-back gives the EPC encode-result feedback (void-and-retry).
///   - <b>WinUSB</b>: experimental / OEM path. If the operator's vendor
///     driver is replaced with WinUSB, raw bulk writes go straight to the
///     endpoints. Limited coverage on a ZR300I — try spooler first.
/// </summary>
public sealed class PrintService : IDisposable
{
    private readonly object _lock = new();
    private PostekTcpTransport? _tcp;
    private PostekUsbTransport? _usb;
    private PostekSpoolerTransport? _spooler;

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
        _tcp = null;
        _usb = null;
        _spooler = null;
        Status = Status with { Online = false, ConnectionType = null };
        RaiseStatus();
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
        lock (_lock) { tcp = _tcp; usb = _usb; spooler = _spooler; }

        try
        {
            if (tcp is not null && tcp.IsConnected)
                return await tcp.SendAsync(pplz, ct).ConfigureAwait(false);
            if (spooler is not null && spooler.IsConnected)
                return await spooler.SendAsync(pplz, ct).ConfigureAwait(false);
            if (usb is not null && usb.IsConnected)
                return await usb.SendAsync(pplz, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            LastError = "Printer disconnected during print.";
            return false;
        }

        LastError = "Printer not connected. Click Connect in Settings.";
        return false;
    }

    public void Dispose() => Disconnect();
}
