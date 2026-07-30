using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dunhill.PrintStudio.Models;

namespace Dunhill.PrintStudio.Services;

/// <summary>
/// Reads/writes <see cref="LabelTemplate"/> JSON files in
/// %LOCALAPPDATA%\DunhillPrintStudio\templates\.
///
/// File naming: {Id}.json (sanitized — Id has whitespace and slashes
/// stripped). Atomic write via .tmp + File.Move so a power-cut during
/// save doesn't leave a half-written template that breaks subsequent loads.
/// </summary>
public sealed class TemplateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _root;

    public TemplateStore()
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DunhillPrintStudio", "templates");
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    /// <summary>Load every template from disk. Returns an empty list on a fresh install.</summary>
    public List<LabelTemplate> LoadAll()
    {
        var list = new List<LabelTemplate>();
        foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var t = JsonSerializer.Deserialize<LabelTemplate>(json, JsonOpts);
                if (t != null) list.Add(t);
            }
            catch (Exception ex)
            {
                Log($"TemplateStore: failed to load {file}: {ex.Message}");
            }
        }
        return list.OrderBy(t => t.Name).ToList();
    }

    public void Save(LabelTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.Id))
            template.Id = Guid.NewGuid().ToString("N")[..8];

        var path = PathForId(template.Id);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(template, JsonOpts);
        File.WriteAllText(tmp, json);
        // Atomic replace — File.Move with overwrite is supported on Windows
        // for files on the same volume (local %LOCALAPPDATA% is).
        File.Move(tmp, path, overwrite: true);
        Log($"TemplateStore: saved {template.Name} → {path}");
    }

    public void Delete(LabelTemplate template)
    {
        var path = PathForId(template.Id);
        if (File.Exists(path)) File.Delete(path);
        Log($"TemplateStore: deleted {template.Name}");
    }

    private string PathForId(string id)
    {
        var safe = SanitizeId(id);
        return Path.Combine(_root, safe + ".json");
    }

    private static string SanitizeId(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = id.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? "template" : s;
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
        catch { }
    }
}