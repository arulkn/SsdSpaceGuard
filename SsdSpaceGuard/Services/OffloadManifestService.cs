using System.IO;
using System.Text.Json;

namespace SsdSpaceGuard.Services;

public sealed class OffloadRecord
{
    public string SourcePath { get; set; } = string.Empty;
    public string DestPath { get; set; } = string.Empty;
    public DateTime OffloadedUtc { get; set; }
}

/// <summary>
/// Persistent record of every NTFS Junction created by SSD Space Guard, stored at
/// %LOCALAPPDATA%\SsdSpaceGuard\offload_manifest.json.
/// Restore deletes the junction's target folder, so it is only allowed for junctions this app
/// demonstrably created (see JunctionMigrationService.ValidateRestoreOwnership).
/// </summary>
public sealed class OffloadManifestService
{
    private static readonly object FileLock = new();
    private readonly string _manifestPath;

    public OffloadManifestService()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SsdSpaceGuard");
        _manifestPath = Path.Combine(dir, "offload_manifest.json");
    }

    public static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');

    public OffloadRecord? Find(string sourcePath)
    {
        string key = Normalize(sourcePath);
        lock (FileLock)
        {
            return Load().FirstOrDefault(r => string.Equals(Normalize(r.SourcePath), key, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Record(string sourcePath, string destPath)
    {
        string key = Normalize(sourcePath);
        lock (FileLock)
        {
            var records = Load();
            records.RemoveAll(r => string.Equals(Normalize(r.SourcePath), key, StringComparison.OrdinalIgnoreCase));
            records.Add(new OffloadRecord { SourcePath = key, DestPath = Normalize(destPath), OffloadedUtc = DateTime.UtcNow });
            Save(records);
        }
    }

    public void Remove(string sourcePath)
    {
        string key = Normalize(sourcePath);
        lock (FileLock)
        {
            var records = Load();
            if (records.RemoveAll(r => string.Equals(Normalize(r.SourcePath), key, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Save(records);
            }
        }
    }

    private List<OffloadRecord> Load()
    {
        try
        {
            if (!File.Exists(_manifestPath)) return new List<OffloadRecord>();
            return JsonSerializer.Deserialize<List<OffloadRecord>>(File.ReadAllText(_manifestPath)) ?? new List<OffloadRecord>();
        }
        catch
        {
            // A corrupt manifest fails safe: managed junctions simply become non-restorable until fixed.
            return new List<OffloadRecord>();
        }
    }

    private void Save(List<OffloadRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
        string tmp = _manifestPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _manifestPath, overwrite: true);
    }
}
