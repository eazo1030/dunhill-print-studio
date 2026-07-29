using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Dunhill.PrintStudio.Usb;

/// <summary>
/// Windows print-spooler transport for the Postek ZR300I. Mirrors the path
/// BarTender itself uses at runtime: raw PPLZ bytes go through
/// <c>OpenPrinter</c> / <c>StartDocPrinter(RAW)</c> / <c>WritePrinter</c>;
/// the Seagull/Postek driver handles the actual USB transport on its end.
///
/// Why this path beats raw WinUSB:
///   - No Zadig, no driver swap. The operator installs the Seagull driver
///     once (it ships with the printer CD), and our app talks to it through
///     Windows print spooler.
///   - BarTender detects Postek the exact same way: <c>LocalPrintServer.GetPrintQueues()</c>.
///   - If the printer's vendor driver ships an RFID-aware .inf for the ZR300I,
///     spooler jobs inherit whatever vendor-mode command sequences it expects.
///
/// Trade-off: spooler path is one-way for PPLZ read-back. For full
/// bidirectional RFID encode feedback (EPC was written ok / void-and-retry),
/// use TCP :9100 instead — same machine, just an Ethernet cable and LCD flip.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PostekSpoolerTransport : IDisposable
{
    public string? LastError { get; private set; }
    public string? PrinterName { get; private set; }
    public bool IsConnected => _handle is not null && !_handle.IsClosed && !_handle.IsInvalid;

    private SafeFileHandle? _handle;
    private int _job;
    private bool _disposed;

    public bool Open(string printerName)
    {
        LastError = null;
        Close();
        if (string.IsNullOrWhiteSpace(printerName))
        {
            LastError = "Printer name is empty.";
            return false;
        }
        var ok = WinspoolNativeMethods.OpenPrinter(printerName, out var hPrinter, IntPtr.Zero);
        if (!ok || hPrinter == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            LastError = $"OpenPrinter({printerName}) failed: Win32 error {err}.";
            return false;
        }
        _handle = new SafeFileHandle(hPrinter, ownsHandle: true);
        PrinterName = printerName;
        return true;
    }

    public async Task<bool> SendAsync(string pplz, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            LastError = "Spooler printer not connected.";
            return false;
        }
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(pplz);
            return await Task.Run(() => WriteRawSync(bytes), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LastError = "Print job cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            LastError = $"Spooler write failed: {ex.Message}";
            return false;
        }
    }

    private bool WriteRawSync(byte[] payload)
    {
        var handle = _handle!;
        var docInfo = new WinspoolNativeMethods.DOCINFOW
        {
            cbSize = (uint)Marshal.SizeOf<WinspoolNativeMethods.DOCINFOW>(),
            docName = "Dunhill Print Studio Job",
            outputFile = null,
            dataType = WinspoolNativeMethods.DATATYPE_RAW_PASS_THROUGH
        };
        var job = WinspoolNativeMethods.StartDocPrinterW(handle, 1, ref docInfo);
        if (job == 0)
        {
            var err = Marshal.GetLastWin32Error();
            LastError = $"StartDocPrinter (RAW) failed: Win32 error {err}. " +
                        "Confirm the selected printer's driver supports RAW pass-through.";
            return false;
        }
        _job = job;
        try
        {
            if (!WinspoolNativeMethods.StartPagePrinter(handle))
            {
                var err = Marshal.GetLastWin32Error();
                LastError = $"StartPagePrinter failed: Win32 error {err}";
                return false;
            }
            try
            {
                var pinned = GCHandle.Alloc(payload, GCHandleType.Pinned);
                try
                {
                    if (!WinspoolNativeMethods.WritePrinter(handle, pinned.AddrOfPinnedObject(),
                            (uint)payload.Length, out var written))
                    {
                        var err = Marshal.GetLastWin32Error();
                        LastError = $"WritePrinter failed: Win32 error {err}";
                        return false;
                    }
                    if (written != (uint)payload.Length)
                    {
                        LastError = $"WritePrinter short write: sent {payload.Length}, ack {written}";
                        return false;
                    }
                    return true;
                }
                finally { pinned.Free(); }
            }
            finally
            {
                WinspoolNativeMethods.EndPagePrinter(handle);
            }
        }
        finally
        {
            WinspoolNativeMethods.EndDocPrinter(handle);
            WinspoolNativeMethods.ClosePrinter(handle);
            _job = 0;
        }
    }

    public void Close()
    {
        if (_handle is { IsClosed: false })
        {
            _handle.Close();
        }
        _handle = null;
        _job = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    /// <summary>Row describing one installed Postek/Seagull print queue.</summary>
    public sealed record SpoolerPrinterInfo(string Name, string DriverName, string PortName, int JobCount);

    /// <summary>
    /// Enumerate every print queue installed on this PC, filtered to those
    /// whose name contains "Postek" (case-insensitive). Operators see the
    /// exact same names Windows lists under Settings → Printers & scanners.
    /// </summary>
    public static IReadOnlyList<SpoolerPrinterInfo> EnumeratePostekPrinters()
    {
        var results = new List<SpoolerPrinterInfo>();
        try
        {
            using var server = new System.Printing.LocalPrintServer();
            foreach (var q in server.GetPrintQueues())
            {
                var name = q.FullName ?? "";
                if (name.IndexOf("Postek", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var driver = TryGet(q, static (System.Printing.PrintQueue pq) => pq.QueueDriver?.Name ?? "");
                var port   = TryGet(q, static (System.Printing.PrintQueue pq) => pq.QueuePort?.Name ?? "");
                var jobs   = TryGet(q, static (System.Printing.PrintQueue pq) => (int)pq.NumberOfJobs);
                results.Add(new SpoolerPrinterInfo(name, driver, port, jobs));
            }
        }
        catch (Exception ex)
        {
            // Surface the error to the caller via the log; the operator sees
            // an empty list and the settings panel explains next steps.
            System.Diagnostics.Debug.WriteLine($"EnumeratePostekPrinters: {ex.Message}");
        }
        return results;
    }

    /// <summary>Enumerate all print queues (no filter) — useful diagnostics.</summary>
    public static IReadOnlyList<SpoolerPrinterInfo> EnumerateAllPrinters()
    {
        var results = new List<SpoolerPrinterInfo>();
        try
        {
            using var server = new System.Printing.LocalPrintServer();
            foreach (var q in server.GetPrintQueues())
            {
                results.Add(new SpoolerPrinterInfo(
                    q.FullName ?? "",
                    TryGet(q, static (System.Printing.PrintQueue pq) => pq.QueueDriver?.Name ?? ""),
                    TryGet(q, static (System.Printing.PrintQueue pq) => pq.QueuePort?.Name ?? ""),
                    TryGet(q, static (System.Printing.PrintQueue pq) => (int)pq.NumberOfJobs)));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EnumerateAllPrinters: {ex.Message}");
        }
        return results;
    }

    private static T TryGet<T>(System.Printing.PrintQueue q, Func<System.Printing.PrintQueue, T> getter)
    {
        try { return getter(q); } catch { return default!; }
    }
}

internal static class WinspoolNativeMethods
{
    private const string WinspoolDll = "winspool.drv";
    public const string DATATYPE_RAW_PASS_THROUGH = "RAW";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DOCINFOW
    {
        public uint cbSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string docName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? outputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string dataType;
    }

    [DllImport(WinspoolDll, SetLastError = true)]
    public static extern bool OpenPrinter(
        [MarshalAs(UnmanagedType.LPWStr)] string szPrinter,
        out IntPtr phPrinter,
        IntPtr pDefault);

    [DllImport(WinspoolDll, SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport(WinspoolDll, SetLastError = true)]
    public static extern int StartDocPrinterW(
        SafeFileHandle hPrinter,
        int level,
        ref DOCINFOW di);

    [DllImport(WinspoolDll, SetLastError = true)]
    public static extern bool EndDocPrinter(SafeFileHandle hPrinter);

    [DllImport(WinspoolDll, SetLastError = true)]
    public static extern bool StartPagePrinter(SafeFileHandle hPrinter);

    [DllImport(WinspoolDll, SetLastError = true)]
    public static extern bool EndPagePrinter(SafeFileHandle hPrinter);

    [DllImport(WinspoolDll, SetLastError = true)]
    public static extern bool WritePrinter(
        SafeFileHandle hPrinter,
        IntPtr pBuf,
        uint cbBuf,
        out uint pcWritten);
}
