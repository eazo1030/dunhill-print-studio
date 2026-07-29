using System.IO;
using System.Text.Json;

namespace Dunhill.PrintStudio.Data;

/// <summary>
/// Local JSON-backed stores for inventory, print queue, and history.
/// No database dep — everything is plain JSON files in %LOCALAPPDATA%\DunhillPrintStudio\.
///
/// For a real warehouse, swap this for SQLite (Microsoft.Data.Sqlite). The interface
/// stays the same.
/// </summary>
public sealed class JsonStore<T> where T : class
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JsonStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public async Task<T?> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path)) return null;
            await using var fs = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<T>(fs, _json, ct);
        }
        catch
        {
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(T data, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var tmp = _path + ".tmp";
            await using (var fs = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(fs, data, _json, ct);
            }
            // Atomic replace so a power loss doesn't corrupt the file
            File.Move(tmp, _path, overwrite: true);
        }
        finally { _gate.Release(); }
    }
}

public static class DataPaths
{
    public static string Folder =>
        Environment.ExpandEnvironmentVariables(
            @"%LOCALAPPDATA%\DunhillPrintStudio");

    public static string Inventory => Path.Combine(Folder, "inventory.json");
    public static string Queue => Path.Combine(Folder, "queue.json");
    public static string History => Path.Combine(Folder, "history.json");
    public static string Settings => Path.Combine(Folder, "settings.json");
}
