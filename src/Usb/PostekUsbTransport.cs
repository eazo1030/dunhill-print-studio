using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Dunhill.PrintStudio.Usb;

/// <summary>
/// WinUSB P/Invoke transport for the Postek ZR300I (VID 0x0FE6).
///
/// Uses the WinUSB driver installed by Zadig. Talks to the printer on bulk
/// endpoints OUT 0x01 (PPLZ payload) and IN 0x81 (status reply), the
/// canonical layout for the ZR300I family. Edit the endpoint constants
/// if your firmware variant uses different numbers.
///
/// Why P/Invoke over LibUsbDotNet or Usb.Net:
///   - Zero nuget deps, zero extra DLLs alongside the .exe
///   - winusb.dll ships with Windows 7+ (always present)
///   - About 200 lines, full control over cancellation and disposal
///   - Matches the TCP transport's contract one-to-one so PrintService.cs
///     stays symmetric.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PostekUsbTransport : IDisposable
{
    private const ushort PostekVendorId = 0x0FE6;

    // Common ZR300I family PIDs across firmware variants. Add yours here
    // if your unit reports a different value after Zadig.
    private static readonly ushort[] DefaultProductIds = { 0x2012, 0x2024, 0x8150 };

    // Standard bulk endpoint addresses for Postek ZR300I (PPLZ variant).
    private const byte BulkOutEndpoint = 0x01;
    private const byte BulkInEndpoint  = 0x81;

    private SafeFileHandle? _deviceHandle;
    private IntPtr _winUsbHandle;
    private bool _disposed;

    public string? LastError { get; private set; }
    public bool IsConnected => _winUsbHandle != IntPtr.Zero;
    public string? DevicePath { get; private set; }
    public ushort DetectedProductId { get; private set; }

    public bool Open(ushort[]? acceptProductIds = null)
    {
        LastError = null;
        Close();

        try
        {
            var pids = acceptProductIds ?? DefaultProductIds;
            var devicePath = FindPostekDevicePath(PostekVendorId, pids);
            if (devicePath is null)
            {
                LastError =
                    "No Postek ZR300I (VID 0x0FE6) found with the WinUSB driver. " +
                    "Plug in the printer and run Zadig: Options -> List All Devices -> " +
                    "pick the ZR300I -> target WinUSB -> Replace Driver, then try again.";
                return false;
            }
            DevicePath = devicePath;
            DetectedProductId = ParseProductIdFromInstancePath(devicePath);

            _deviceHandle = NativeMethods.CreateFile(
                devicePath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);
            if (_deviceHandle.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                LastError = $"CreateFile failed on device path: Win32 error {err}";
                _deviceHandle.Dispose();
                _deviceHandle = null;
                return false;
            }

            if (!NativeMethods.WinUsb_Initialize(_deviceHandle, out _winUsbHandle))
            {
                var err = Marshal.GetLastWin32Error();
                LastError = $"WinUsb_Initialize failed: Win32 error {err}";
                Close();
                return false;
            }

            var pipeInfo = new NativeMethods.WINUSB_PIPE_INFORMATION();
            var outOk = NativeMethods.WinUsb_QueryPipe(_winUsbHandle, 0, BulkOutEndpoint, ref pipeInfo);
            var inOk  = NativeMethods.WinUsb_QueryPipe(_winUsbHandle, 0, BulkInEndpoint,  ref pipeInfo);
            if (!outOk || !inOk)
            {
                LastError = $"Endpoint 0x{BulkOutEndpoint:X2} OUT or 0x{BulkInEndpoint:X2} IN " +
                            "not found on interface 0. Check that Zadig installed WinUSB for " +
                            "the correct interface (try interface 1 or 2).";
                Close();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = $"USB open failed: {ex.Message}";
            Close();
            return false;
        }
    }

    public async Task<bool> SendAsync(string pplz, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            LastError = "USB printer not connected.";
            return false;
        }
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(pplz);
            return await Task.Run(() => WriteBulkSync(bytes), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LastError = "USB write cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            LastError = $"USB write failed: {ex.Message}";
            return false;
        }
    }

    public async Task<byte[]?> ReadStatusAsync(int maxBytes = 64, CancellationToken ct = default)
    {
        if (!IsConnected) return null;
        try
        {
            return await Task.Run(() =>
            {
                var buf = new byte[maxBytes];
                var ok = NativeMethods.WinUsb_ReadPipe(
                    _winUsbHandle, BulkInEndpoint, buf, (uint)maxBytes, out var read, IntPtr.Zero);
                if (!ok)
                {
                    var err = Marshal.GetLastWin32Error();
                    LastError = err switch
                    {
                        1164     => null,
                        0x57     => null, // pipe closed mid-call
                        _        => $"WinUsb_ReadPipe Win32 error {err}"
                    };
                    return null;
                }
                return read > 0 ? buf[..(int)read] : null;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { LastError = $"USB read failed: {ex.Message}"; return null; }
    }

    public void Close()
    {
        if (_winUsbHandle != IntPtr.Zero)
        {
            NativeMethods.WinUsb_Free(_winUsbHandle);
            _winUsbHandle = IntPtr.Zero;
        }
        if (_deviceHandle != null && !_deviceHandle.IsClosed)
        {
            _deviceHandle.Dispose();
        }
        _deviceHandle = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    private bool WriteBulkSync(byte[] payload)
    {
        var ok = NativeMethods.WinUsb_WritePipe(
            _winUsbHandle,
            BulkOutEndpoint,
            payload,
            (uint)payload.Length,
            out var written,
            IntPtr.Zero);
        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            LastError = $"WinUsb_WritePipe Win32 error {err}";
            return false;
        }
        if ((uint)payload.Length != written)
        {
            LastError = $"Partial USB write: sent {payload.Length}, acknowledged {written}";
            return false;
        }
        return true;
    }

    private static ushort ParseProductIdFromInstancePath(string path)
    {
        var i = path.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + 8 > path.Length) return 0;
        return ushort.TryParse(path.Substring(i + 4, 4),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var pid) ? pid : (ushort)0;
    }

    private static string? FindPostekDevicePath(ushort vid, ushort[] acceptPids)
    {
        // GUID_DEVINTERFACE_WINUSB
        var classGuid = new Guid("{dee8247e-ee62-434a-bf9a-88affaf91b04}");

        var devInfo = NativeMethods.SetupDiGetClassDevs(
            IntPtr.Zero,
            "USB",
            IntPtr.Zero,
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
        if (devInfo.ToInt64() == -1) return null;

        var iface = new NativeMethods.SP_DEVICE_INTERFACE_DATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DATA>()
        };

        int idx = 0;
        string? match = null;
        while (NativeMethods.SetupDiEnumDeviceInterfaces(
                   devInfo, IntPtr.Zero, ref classGuid, idx++, ref iface))
        {
            // First call: get required buffer size; pass an uninitialized detail struct.
            // Marshalling handles plain structs here — we don't need SkipInit because
            // the API only reads cbSize and writes required.
            var detailProbe = default(NativeMethods.SP_DEVICE_INTERFACE_DETAIL_DATA);
            NativeMethods.SetupDiGetDeviceInterfaceDetail(
                devInfo, ref iface, ref detailProbe, 0, out var required, IntPtr.Zero);

            var detail = new NativeMethods.SP_DEVICE_INTERFACE_DETAIL_DATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DETAIL_DATA>()
            };
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                    devInfo, ref iface, ref detail, required, out _, IntPtr.Zero))
                continue;

            var path = detail.DevicePath ?? "";
            if (path.IndexOf($"VID_{vid:X4}", StringComparison.OrdinalIgnoreCase) < 0) continue;

            foreach (var pid in acceptPids)
            {
                if (path.IndexOf($"PID_{pid:X4}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    match = path;
                    break;
                }
            }
            if (match != null) break;
        }

        // Fallback: scan the raw USB class. WinUSB-GUID sometimes misses
        // Zadig-installed instances; the raw class always sees USB devices.
        if (match == null)
        {
            var usbClassGuid = new Guid("{a5dcbf10-6530-11d2-901f-00c04fb951fa}");
            var usbInfo = NativeMethods.SetupDiGetClassDevs(
                IntPtr.Zero, "USB", IntPtr.Zero,
                NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
            var usbIface = new NativeMethods.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DATA>() };
            int j = 0;
            while (NativeMethods.SetupDiEnumDeviceInterfaces(
                       usbInfo, IntPtr.Zero, ref usbClassGuid, j++, ref usbIface))
            {
                var detailProbe = default(NativeMethods.SP_DEVICE_INTERFACE_DETAIL_DATA);
                NativeMethods.SetupDiGetDeviceInterfaceDetail(
                    usbInfo, ref usbIface, ref detailProbe, 0, out var required, IntPtr.Zero);
                var detail = new NativeMethods.SP_DEVICE_INTERFACE_DETAIL_DATA { cbSize = Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DETAIL_DATA>() };
                if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(usbInfo, ref usbIface, ref detail, required, out _, IntPtr.Zero))
                    continue;
                var path = detail.DevicePath ?? "";
                if (path.IndexOf($"VID_{vid:X4}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    match = path;
                    break;
                }
            }
            NativeMethods.SetupDiDestroyDeviceInfoList(usbInfo);
        }

        NativeMethods.SetupDiDestroyDeviceInfoList(devInfo);
        return match;
    }
}

internal static class NativeMethods
{
    private const string WinUsbDll = "winusb.dll";
    private const string SetupApiDll = "setupapi.dll";
    private const string Kernel32Dll = "kernel32.dll";

    public const int DIGCF_PRESENT = 0x02;
    public const int DIGCF_DEVICEINTERFACE = 0x10;

    // Win32 CreateFile access/share/mode/flag constants from <winbase.h>.
    // We pass them OR'd together as a single `uint` to CreateFile's `uint
    // dwDesiredAccess`, `uint dwShareMode`, `uint dwCreationDisposition`,
    // and `uint dwFlagsAndAttributes` parameters.
    public const uint GENERIC_READ        = 0x80000000;
    public const uint GENERIC_WRITE       = 0x40000000;
    public const uint FILE_SHARE_READ     = 0x00000001;
    public const uint FILE_SHARE_WRITE    = 0x00000002;
    public const uint OPEN_EXISTING       = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    // (Bare winnt.h: GENERIC_WRITE and FILE_FLAG_OVERLAPPED share bit
    //  0x40000000; the kernel reads them from different DWORD fields so
    //  the overlap is harmless.)

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SP_DEVICE_INTERFACE_DETAIL_DATA
    {
        public int cbSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string? DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINUSB_PIPE_INFORMATION
    {
        public byte PipeType;
        public byte PipeDirection;
        public ushort MaxPacketSize;
        public byte Interval;
        public byte InterfaceNumber;
    }

    [DllImport(Kernel32Dll, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport(WinUsbDll, SetLastError = true)]
    public static extern bool WinUsb_Initialize(SafeFileHandle DeviceHandle, out IntPtr InterfaceHandle);

    [DllImport(WinUsbDll)]
    public static extern bool WinUsb_Free(IntPtr InterfaceHandle);

    [DllImport(WinUsbDll, SetLastError = true)]
    public static extern bool WinUsb_QueryPipe(
        IntPtr InterfaceHandle,
        byte AlternateSetting,
        byte EndpointIndex,
        ref WINUSB_PIPE_INFORMATION PipeInformation);

    [DllImport(WinUsbDll, SetLastError = true)]
    public static extern bool WinUsb_WritePipe(
        IntPtr InterfaceHandle,
        byte PipeID,
        byte[] Buffer,
        uint BufferLength,
        out uint BytesWritten,
        IntPtr Overlapped);

    [DllImport(WinUsbDll, SetLastError = true)]
    public static extern bool WinUsb_ReadPipe(
        IntPtr InterfaceHandle,
        byte PipeID,
        byte[] Buffer,
        uint BufferLength,
        out uint BytesTransferred,
        IntPtr Overlapped);

    [DllImport(SetupApiDll, EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(
        IntPtr ClassGuid,
        string? Enumerator,
        IntPtr hwndParent,
        int Flags);

    [DllImport(SetupApiDll, EntryPoint = "SetupDiEnumDeviceInterfaces", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet,
        IntPtr DeviceInfoData,
        ref Guid InterfaceClassGuid,
        int MemberIndex,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport(SetupApiDll, EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        ref SP_DEVICE_INTERFACE_DETAIL_DATA DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize,
        IntPtr DeviceInfoData);

    [DllImport(SetupApiDll, SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
}
