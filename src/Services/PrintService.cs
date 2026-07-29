using Dunhill.PrintStudio.Models;
using Dunhill.PrintStudio.Pplz;
using Dunhill.PrintStudio.Usb;

namespace Dunhill.PrintStudio.Services;

/// <summary>
/// Owns the active printer connection (USB or TCP) and exposes a single
/// <see cref="PrintAsync"/> entry point. UI calls this; UI never touches
/// the USB/TCP transports directly. Makes mocking easy.
/// </summary>
public sealed class PrintService : IDisposable
{
    private readonly object _lock = new();
    private PostekUsbTransport? _usb;
    private PostekTcpTransport? _tcp;

    public PrinterStatus Status { get; private set; } = new(
        false, null, null, "Not connected", DateTime.UtcNow);

    public event EventHandler<PrinterStatus>? StatusChanged;

    public string? LastError { get; private set; }

    private void RaiseStatus()
    {
        StatusChanged?.Invoke(this, Status);
    }

    public IReadOnlyList<PostekDeviceInfo> DiscoverUsb() => PostekUsbTransport.Enumerate();

    public bool ConnectUsb(ushort? productId = null)
    {
        lock (_lock)
        {
            Disconnect();
            _usb = new PostekUsbTransport();
            if (!_usb.Open(productId))
            {
                LastError = _usb.LastError;
                Status = Status with { Online = false, LastError = LastError };
                _usb.Dispose();
                _usb = null;
                RaiseStatus();
                return false;
            }
            Status = new PrinterStatus(
                Online: true,
                Model: _usb.ConnectedModel,
                ConnectionType: "USB",
                LastError: null,
                LastChecked: DateTime.UtcNow);
            RaiseStatus();
            return true;
        }
    }

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

    public void Disconnect()
    {
        _usb?.Dispose(); _usb = null;
        _tcp?.Dispose(); _tcp = null;
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

    private async Task<bool> SendRawAsync(string pplz, CancellationToken ct)
    {
        // Snapshot the active transport under the lock, then await OUTSIDE it.
        // Never block the threadpool inside a lock holding section — that's a deadlock factory.
        PostekUsbTransport? usb;
        PostekTcpTransport? tcp;
        lock (_lock)
        {
            usb = _usb;
            tcp = _tcp;
        }

        try
        {
            if (usb != null && usb.IsConnected)
                return await usb.SendAsync(pplz).ConfigureAwait(false);
            if (tcp != null && tcp.IsConnected)
                return await tcp.SendAsync(pplz, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Transport got disposed between snapshot and call. Treat as offline.
            LastError = "Printer disconnected during print.";
            return false;
        }

        LastError = "Printer not connected. Click Connect in Settings.";
        return false;
    }

    public void Dispose() => Disconnect();
}
