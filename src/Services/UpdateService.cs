using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Dunhill.PrintStudio.Services;

/// <summary>
/// Self-updater wrapper around Velopack. The current GitHub release is the
/// "from" version; the next tagged release (`v*`) becomes the "to" version.
/// All HTTP traffic is against the public dunhill-print-studio GitHub repo.
/// </summary>
public sealed class UpdateService
{
    private const string GitHubRepoUrl = "https://github.com/eazo1030/dunhill-print-studio";

    private readonly UpdateManager _mgr;

    public UpdateService()
    {
        _mgr = new UpdateManager(new GithubSource(GitHubRepoUrl, accessToken: null, prerelease: false));
    }

    /// <summary>Version of the binary currently running (e.g. "1.0.0").</summary>
    public string CurrentVersion => _mgr.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>True when the app was launched from a Velopack install (vs. a raw .exe).</summary>
    public bool IsInstalled => _mgr.IsInstalled;

    /// <summary>
    /// Check GitHub Releases for a newer version. Returns null when up-to-date.
    /// Throws on network / parse errors — caller surfaces to the operator.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        Log($"CheckForUpdates: current={CurrentVersion} installed={IsInstalled}");
        try
        {
            var info = await _mgr.CheckForUpdatesAsync();
            Log($"CheckForUpdates: target={info?.TargetFullRelease?.Version?.ToString() ?? "none"}");
            return info;
        }
        catch (Exception ex)
        {
            Log("CheckForUpdates FAILED: " + ex);
            throw;
        }
    }

    /// <summary>
    /// Download the delta/full release payload into the Velopack staging dir.
    /// Does NOT restart. Call <see cref="ApplyUpdatesAndExit"/> next.
    /// </summary>
    public async Task DownloadUpdatesAsync(UpdateInfo info)
    {
        Log($"DownloadUpdates: target={info.TargetFullRelease.Version}");
        await _mgr.DownloadUpdatesAsync(info);
        Log("DownloadUpdates: complete");
    }

    /// <summary>
    /// Atomically replace the current install and relaunch the app.
    /// MUST be called on the UI thread so WPF can shut down cleanly.
    /// Process exits inside this call.
    /// </summary>
    public void ApplyUpdatesAndExit(UpdateInfo info)
    {
        Log($"ApplyUpdatesAndExit: target={info.TargetFullRelease.Version}");
        _mgr.ApplyUpdatesAndExit(info);
    }

    private static void Log(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DunhillPrintStudio");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "startup.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { /* swallow */ }
    }
}