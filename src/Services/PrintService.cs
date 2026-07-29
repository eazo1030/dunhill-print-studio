using Dunhill.PrintStudio.Models;
using Dunhill.PrintStudio.Pplz;
using Dunhill.PrintStudio.Usb;

namespace Dunhill.PrintStudio.Services;

/// <summary>
/// Owns the active printer connection (currently TCP only) and exposes a
/// single <see cref="PrintAsync"/> entry point. UI calls this; UI never
/// touches the transport directly.
///
/// USB support was removed in Phase 1 because LibUsbDotNet's 3.x API
/// diverged significantly from 2.x. To add USB later, use Usb.Net 4.x
/// or write a P/Invoke wrapper around winusb.dll. The TCP path works
/// for any ZR300I on a reachable network.
/// </summary>
public sealed class PrintService : IDisposable
{
    private readonly object _lock = new();
    private PostekTcpTransport? _tcp;
    private PostekUsbTransport? _usb;
    private string? _currentEndpoint;

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
            _currentEndpoint = $"TCP {host}:{port}";
            Status = new PrinterStatus(
                Online: true,
                Model: "ZR300I (network)",
                ConnectionType: _currentEndpoint,
                LastError: null,
                LastChecked: DateTime.UtcNow);
            RaiseStatus();
            return true;
        }
    }

    public bool ConnectUsb(ushort[]? acceptProductIds = null)
    {
        lock (_lock)
        {
            Disconnect();
            _usb = new PostekUsbTransport();
            if (!_usb.Open(acceptProductIds))
            {
                LastError = _usb.LastError;
                Status = Status with { Online = false, LastError = LastError };
                _usb.Dispose();
                _usb = null;
                RaiseStatus();
                return false;
            }
            var pid = _usb.DetectedProductId != 0 ? $" PID 0x{_usb.DetectedProductId:X4}" : "";
            _currentEndpoint = $"USB{pid}";
            Status = new PrinterStatus(
                Online: true,
                Model: "ZR300I (USB)",
                ConnectionType: _currentEndpoint,
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
        _tcp = null;
        _usb = null;
        _currentEndpoint = null;
        Status = Status with { Online = false, ConnectionType = null };
        RaiseStatus();
    }

    /// <summary>
    /// Print one label. Returns true on success, false on any transport error.
    /// </summary>
    public async Task<bool> PrintAsync(LabelSpec spec, LabelDimensions dims, CancellationToken ct = default)
    {
        var pplz = PplzBuilder.BuildItemLabel(spec, dims);
        return await SendRawAsync(pplz, ct);
    }

    /// <summary>
    /// Print a health-check label — useful from the Settings panel to verify
    /// the connection without needing a real inventory item.
    /// </summary>
    public async Task<bool> PrintTestLabelAsync(LabelDimensions dims, CancellationToken ct = default)
    {
        var pplz = PplzBuilder.BuildTestLabel(dims);
        return await SendRawAsync(pplz, ct);
    }

    public async Task<byte[]?> ReadStatusAsync(int maxBytes = 64, CancellationToken ct = default)
    {
        PostekTcpTransport? tcp; PostekUsbTransport? usb;
        lock (_lock) { tcp = _tcp; usb = _usb; }
        try
        {
            if (tcp != null && tcp.IsConnected) return await tcp.ReadStatusAsync(maxBytes, ct).ConfigureAwait(false);
            if (usb != null && usb.IsConnected) return await usb.ReadStatusAsync(maxBytes, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        return null;
    }

    private async Task<bool> SendRawAsync(string pplz, CancellationToken ct)
    {
        // Snapshot the active transports under the lock, then await OUTSIDE it.
        PostekTcpTransport? tcp; PostekUsbTransport? usb;
        lock (_lock) { tcp = _tcp; usb = _usb; }

        try
        {
            if (tcp != null && tcp.IsConnected)
                return await tcp.SendAsync(pplz, ct).ConfigureAwait(false);
            if (usb != null && usb.IsConnected)
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
