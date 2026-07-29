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

    public void Disconnect()
    {
        _tcp?.Dispose();
        _tcp = null;
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
        PostekTcpTransport? tcp;
        lock (_lock) { tcp = _tcp; }

        try
        {
            if (tcp != null && tcp.IsConnected)
                return await tcp.SendAsync(pplz, ct).ConfigureAwait(false);
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
