using System.Collections.Concurrent;
using System.IO;
using SsdSpaceGuard.Models;

namespace SsdSpaceGuard.Services;

public class DiskScannerService
{
    private readonly ClassificationService _classifier;

    public DiskScannerService(ClassificationService classifier)
    {
        _classifier = classifier;
    }

    public string GetOsDriveRoot()
    {
        string sysDir = Environment.SystemDirectory;
        return Path.GetPathRoot(sysDir) ?? "C:\\";
    }

    public List<DriveStats> GetReadyFixedDrives()
    {
        string osRoot = GetOsDriveRoot();
        var list = new List<DriveStats>();

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                list.Add(new DriveStats
                {
                    Name = d.Name,
                    VolumeLabel = d.VolumeLabel,
                    DriveFormat = d.DriveFormat,
                    TotalBytes = d.TotalSize,
                    FreeBytes = d.AvailableFreeSpace,
                    IsOsDrive = string.Equals(d.Name, osRoot, StringComparison.OrdinalIgnoreCase)
                });
            }
            catch { }
        }

        return list
            .OrderByDescending(d => d.IsOsDrive)
            .ThenByDescending(d => d.FreeBytes)
            .ToList();
    }

    /// <summary>
    /// Fast iterative directory size computation.
    /// If the root directory itself is a Junction/ReparsePoint and followRootLink is true,
    /// computes the size of the linked target so the user can see how much space is offloaded.
    /// Inner reparse points are skipped to prevent infinite loops or double counting.
    /// </summary>
    public static long ComputeDirectorySize(string path, bool followRootLink = true, CancellationToken ct = default)
    {
        long total = 0;
        var stack = new Stack<DirectoryInfo>();

        try
        {
            var root = new DirectoryInfo(path);
            if (!root.Exists) return 0;
            bool isReparse = (root.Attributes & FileAttributes.ReparsePoint) != 0;
            if (isReparse && !followRootLink) return 0;
            stack.Push(root);
        }
        catch
        {
            return 0;
        }

        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) break;
            var current = stack.Pop();

            try
            {
                foreach (var fi in current.EnumerateFiles())
                {
                    try { total += fi.Length; } catch { }
                }
            }
            catch { }

            try
            {
                foreach (var sub in current.EnumerateDirectories())
                {
                    try
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            stack.Push(sub);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        return total;
    }

    public static (bool IsJunction, string? TargetPath) InspectReparsePoint(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            if (!di.Exists) return (false, null);
            bool isReparse = (di.Attributes & FileAttributes.ReparsePoint) != 0;
            if (!isReparse) return (false, null);
            return (true, di.LinkTarget ?? "ReparsePoint (Junction/Symlink)");
        }
        catch
        {
            return (false, null);
        }
    }

    public async Task<List<ScannedFolderItem>> ScanStandardAndCustomRootsAsync(
        long minSizeBytes,
        IEnumerable<string> customRoots,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidateDirs = new List<(string Path, string Scope)>();

        void AddChildren(string parentPath, string scopeLabel, Func<string, bool>? filter = null)
        {
            if (string.IsNullOrWhiteSpace(parentPath) || !Directory.Exists(parentPath)) return;
            try
            {
                foreach (var dir in Directory.GetDirectories(parentPath))
                {
                    if (filter != null && !filter(dir)) continue;
                    candidateDirs.Add((dir, scopeLabel));
                }
            }
            catch { }
        }

        // 1. AppData\Local (excluding Programs & Packages which are expanded separately for granular control)
        AddChildren(localAppData, "AppData\\Local", d =>
        {
            string name = Path.GetFileName(d);
            return !string.Equals(name, "Programs", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(name, "Packages", StringComparison.OrdinalIgnoreCase);
        });

        // 2. AppData\Local\Programs (individual per-user installed apps like Ollama, VS Code, Python)
        AddChildren(Path.Combine(localAppData, "Programs"), "AppData\\Local\\Programs");

        // 3. AppData\Local\Packages (individual UWP / WSL2 packages)
        AddChildren(Path.Combine(localAppData, "Packages"), "AppData\\Local\\Packages");

        // 4. Browser on-device AI models (e.g. Chrome/Edge OptGuideOnDeviceModel which can be 4+ GB alone)
        foreach (var browser in _classifier.Rules.BrowserRoots)
        {
            string userDataDir = Path.Combine(localAppData, browser.RelativeUserDataPath);
            string optGuide = Path.Combine(userDataDir, "OptGuideOnDeviceModel");
            if (Directory.Exists(optGuide))
            {
                candidateDirs.Add((optGuide, $"{browser.VendorName} AI Model"));
            }
        }

        // 5. AppData\Roaming
        AddChildren(roamingAppData, "AppData\\Roaming");

        // 6. UserProfile dot-folders (.ollama, .vscode, .cache, .nuget, .gemini, .cursor, etc.) and heavy folders
        AddChildren(userProfile, "UserProfile", d =>
        {
            string name = Path.GetFileName(d);
            return !string.Equals(name, "AppData", StringComparison.OrdinalIgnoreCase);
        });

        // 7. Custom user-added directories
        foreach (var custom in customRoots)
        {
            if (Directory.Exists(custom))
            {
                candidateDirs.Add((custom, "Custom Folder"));
            }
        }

        // Deduplicate by full path
        var uniqueCandidates = candidateDirs
            .GroupBy(c => Path.GetFullPath(c.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        progress?.Report($"Scanning {uniqueCandidates.Count} directories in parallel...");

        var bag = new ConcurrentBag<ScannedFolderItem>();
        int completed = 0;

        await Task.Run(() =>
        {
            Parallel.ForEach(uniqueCandidates, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
                CancellationToken = ct
            }, candidate =>
            {
                var (isJunction, targetPath) = InspectReparsePoint(candidate.Path);
                var (classification, reason) = _classifier.ClassifyDirectory(candidate.Path, isJunction, targetPath);

                // Only follow root reparse points when they are genuine cross-drive offloaded junctions,
                // avoiding redundant traversal of same-drive Windows compatibility junctions (e.g. My Documents -> Documents).
                bool isOffloadedJunction = classification == FolderClassification.AlreadyOffloadedJunction;
                long size = ComputeDirectorySize(candidate.Path, followRootLink: !isJunction || isOffloadedJunction, ct);

                int done = Interlocked.Increment(ref completed);
                if (done % 15 == 0)
                {
                    progress?.Report($"Scanned {done}/{uniqueCandidates.Count} folders...");
                }

                // Always include active cross-drive offloaded junctions or folders meeting the minimum size threshold
                if (size >= minSizeBytes || isOffloadedJunction)
                {
                    bag.Add(new ScannedFolderItem
                    {
                        Name = Path.GetFileName(candidate.Path),
                        FullPath = candidate.Path,
                        ScopeCategory = candidate.Scope,
                        SizeBytes = size,
                        IsJunction = isJunction,
                        JunctionTarget = targetPath,
                        Classification = classification,
                        RecommendationReason = reason,
                        StatusText = isOffloadedJunction ? "Offloaded" : "On OS Drive"
                    });
                }
            });
        }, ct);

        return bag
            .OrderByDescending(x => x.SizeBytes)
            .ToList();
    }
}
