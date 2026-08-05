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

    // Fine-grained PAT scoped to read-only on dunhill-print-studio. Required so
    // Velopack can fetch the RELEASES manifest + .nupkg from this PRIVATE repo.
    // Anonymous access was broken when the repo went private. The PAT is only
    // good for read access to releases; it has no write scope on any repo.
    // Rotation: generate a new fine-grained PAT in the GitHub web UI with
    // Contents=Read on dunhill-print-studio and replace this string.
    private const string UpdateAccessToken = "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";

    private readonly UpdateManager _mgr;

    public UpdateService()
    {
        _mgr = new UpdateManager(new GithubSource(GitHubRepoUrl, accessToken: UpdateAccessToken, prerelease: false));
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
    /// Apply a Velopack update and restart the app.
    ///
    /// v1.2.11 fix: previously this called <c>ApplyUpdatesAndExit</c>,
    /// which terminates the WPF process without launching
    /// <c>Update.exe</c> to re-launch the new version. Result: the
    /// app window closed silently and the user had to restart Print
    /// Studio by hand to pick up the new bits. The correct Velopack
    /// API for "exit AND restart me" is <c>ApplyUpdatesAndRestart</c>,
    /// which stages the new version, exits the current process, and
    /// launches Update.exe — Update.exe atomically applies the staged
    /// payload and re-launches the app for us.
    ///
    /// MUST be called on the UI thread so WPF can shut down cleanly.
    /// Process exits inside this call.
    /// </summary>
    public void ApplyUpdatesAndExit(UpdateInfo info)
    {
        Log($"ApplyUpdatesAndRestart: target={info.TargetFullRelease.Version}");
        _mgr.ApplyUpdatesAndRestart(info);
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