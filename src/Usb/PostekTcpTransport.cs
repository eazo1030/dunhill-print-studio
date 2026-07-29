using System.Net.Sockets;

namespace Dunhill.PrintStudio.Usb;

/// <summary>
/// TCP raw-socket transport. Identical wire format to USB: PPLZ bytes over a
/// persistent connection to the printer's port 9100. This is what the Postek
/// "Bartender UL" software does under the hood.
///
/// Pros over USB:
///   - Multiple workstations can share one printer
///   - No driver install needed (Zadig / WinUSB not required)
///   - Works over VLANs, switches, and even Tailscale
/// </summary>
public sealed class PostekTcpTransport : IDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _disposed;

    public string? LastError { get; private set; }
    public bool IsConnected => _client?.Connected == true && _stream != null;
    public string? Host { get; private set; }
    public int Port { get; private set; }

    public bool Open(string host, int port = 9100, int timeoutMs = 3000)
    {
        LastError = null;
        try
        {
            var client = new TcpClient { NoDelay = true };
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(timeoutMs))
            {
                LastError = $"Connection to {host}:{port} timed out after {timeoutMs}ms.";
                client.Close();
                return false;
            }
            if (!task.IsCompletedSuccessfully || !client.Connected)
            {
                LastError = $"Could not connect to {host}:{port}.";
                client.Close();
                return false;
            }
            _client = client;
            _stream = client.GetStream();
            Host = host;
            Port = port;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"TCP open failed: {ex.Message}";
            return false;
        }
    }

    public async Task<bool> SendAsync(string pplz, CancellationToken ct = default)
    {
        if (_stream == null)
        {
            LastError = "Printer not connected.";
            return false;
        }
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(pplz);
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"TCP write failed: {ex.Message}";
            return false;
        }
    }

    public async Task<byte[]?> ReadStatusAsync(int maxBytes = 64, CancellationToken ct = default)
    {
        if (_stream == null) return null;
        try
        {
            var buf = new byte[maxBytes];
            // Use a short read timeout via cancellation
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            var read = await _stream.ReadAsync(buf, timeout.Token);
            return read > 0 ? buf[..read] : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { LastError = $"TCP read failed: {ex.Message}"; return null; }
    }

    public void Close()
    {
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }
}
