using System.Management;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace Dunhill.PrintStudio.Usb;

/// <summary>
/// USB transport for Postek label printers. Discovers devices by VID/PID,
/// opens an interface, and writes PPLZ command strings as raw bytes.
///
/// Postek VID: 0x0FE6 (Post-Bar Code Ltd.)
/// Common PIDs (auto-discovered):
///   - 0x6001 / 0x6002  — older TX series
///   - 0x6201          — ZR300I (current generation)
///   - 0x6301          — ZR500I
///
/// NOTE: On Windows, the OS driver for the printer MUST be WinUSB (libusb-compatible),
/// not the Postek vendor driver. If the user installs the vendor .inf first, this
/// transport will fail. The Settings window will warn and offer to install a WinUSB
/// driver via Zadig.
/// </summary>
public sealed class PostekUsbTransport : IDisposable
{
    private const ushort POSTEK_VENDOR_ID = 0x0FE6;

    private UsbDevice? _device;
    private UsbEndpointWriter? _writer;
    private UsbEndpointReader? _reader;
    private bool _disposed;

    public string? LastError { get; private set; }
    public bool IsConnected => _device is { IsOpen: true };
    public string? ConnectedModel { get; private set; }
    public string? ConnectedSerial { get; private set; }

    /// <summary>
    /// Enumerate all Postek USB devices currently attached to the host.
    /// Returns a list of (vid, pid, product name, serial number) tuples.
    /// </summary>
    public static IReadOnlyList<PostekDeviceInfo> Enumerate()
    {
        var results = new List<PostekDeviceInfo>();

        // Method 1: WMI — works without libusb init, gives us friendly names
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, PNPDeviceID, Manufacturer FROM Win32_PnPEntity " +
                "WHERE PNPDeviceID LIKE 'USB\\\\VID_0FE6%' OR Name LIKE '%Postek%'");
            foreach (var obj in searcher.Get())
            {
                var pnpId = obj["PNPDeviceID"]?.ToString() ?? "";
                var name = obj["Name"]?.ToString() ?? "Postek printer";
                // Parse VID/PID from PNPDeviceID: USB\VID_0FE6&PID_6201\...
                var vid = ExtractHex(pnpId, "VID_");
                var pid = ExtractHex(pnpId, "PID_");
                results.Add(new PostekDeviceInfo(vid, pid, name, pnpId));
            }
        }
        catch { /* WMI may not be available on all SKUs */ }

        // Method 2: libusb enumeration — returns VID/PID for actually-openable devices
        try
        {
            using var ctx = new UsbContext();
            ctx.Start();
            var devices = ctx.List();
            foreach (var dev in devices)
            {
                if (dev.VendorId != POSTEK_VENDOR_ID) continue;
                results.Add(new PostekDeviceInfo(
                    $"0x{dev.VendorId:X4}",
                    $"0x{dev.ProductId:X4}",
                    dev.ManufacturerProduct ?? "Postek printer",
                    dev.DevicePath ?? ""));
            }
        }
        catch { /* libusb enumeration may fail if WinUSB driver not installed */ }

        return results;
    }

    /// <summary>
    /// Open the first available Postek printer, or a specific PID if provided.
    /// </summary>
    public bool Open(ushort? productId = null)
    {
        LastError = null;
        try
        {
            using var ctx = new UsbContext();
            ctx.Start();

            // Find the right device
            var devices = ctx.List();
            UsbDevice? target = null;
            foreach (var dev in devices)
            {
                if (dev.VendorId != POSTEK_VENDOR_ID) continue;
                if (productId.HasValue && dev.ProductId != productId.Value) continue;
                target = dev;
                break;
            }

            if (target == null)
            {
                LastError = $"No Postek printer found (VID 0x{POSTEK_VENDOR_ID:X4}). " +
                            "Check USB cable and ensure WinUSB driver is installed (Zadig).";
                return false;
            }

            // Open and claim interface 0 (printer class interface)
            if (!target.Open())
            {
                LastError = $"Failed to open USB device {target.ProductId:X4}.";
                return false;
            }
            if (!target.ClaimInterface(0))
            {
                LastError = "Failed to claim USB interface 0. Another application may be using the printer.";
                target.Close();
                return false;
            }

            _device = target;
            _writer = target.OpenEndpointWriter(WriteEndpointID.Ep01, EndpointType.Bulk);
            _reader = target.OpenEndpointReader(ReadEndpointID.Ep01, 64, EndpointType.Bulk);

            ConnectedModel = target.ManufacturerProduct ?? "ZR300I";
            ConnectedSerial = target.SerialNumber;

            return true;
        }
        catch (Exception ex)
        {
            LastError = $"USB open failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Send a PPLZ command string to the printer.
    /// </summary>
    public Task<bool> SendAsync(string pplz)
    {
        if (_writer == null || _device?.IsOpen != true)
        {
            LastError = "Printer not connected.";
            return Task.FromResult(false);
        }

        return Task.Run(() =>
        {
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(pplz);
                var ec = _writer.Write(bytes, 2000, out var written);
                if (ec != ErrorCode.None)
                {
                    LastError = $"USB write failed: {ec}";
                    return false;
                }
                if (written != bytes.Length)
                {
                    LastError = $"Partial write: {written}/{bytes.Length} bytes.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"USB write exception: {ex.Message}";
                return false;
            }
        });
    }

    /// <summary>
    /// Read up to <paramref name="maxBytes"/> from the printer's status endpoint.
    /// ZR300I returns host status bytes when ~HS is sent.
    /// </summary>
    public Task<byte[]?> ReadStatusAsync(int maxBytes = 64)
    {
        if (_reader == null) return Task.FromResult<byte[]?>(null);

        return Task.Run<byte[]?>(() =>
        {
            try
            {
                var buf = new byte[maxBytes];
                var ec = _reader.Read(buf, 2000, out var read);
                return ec == ErrorCode.None && read > 0 ? buf[..read] : null;
            }
            catch { return null; }
        });
    }

    public void Close()
    {
        if (_device is { IsOpen: true })
        {
            try { _device.ReleaseInterface(0); } catch { }
            try { _device.Close(); } catch { }
        }
        _writer = null;
        _reader = null;
        _device = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    private static string ExtractHex(string s, string prefix)
    {
        var idx = s.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        var sub = s[(idx + prefix.Length)..];
        var hex = new string(sub.TakeWhile(c => Uri.IsHexDigit(c)).ToArray());
        return $"0x{hex}";
    }
}

public sealed record PostekDeviceInfo(
    string VendorId,
    string ProductId,
    string ProductName,
    string DevicePath
);
