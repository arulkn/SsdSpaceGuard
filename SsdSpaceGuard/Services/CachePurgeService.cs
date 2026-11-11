using System.IO;
using SsdSpaceGuard.Models;

namespace SsdSpaceGuard.Services;

public class CachePurgeService
{
    private readonly ClassificationService _classifier;

    public CachePurgeService(ClassificationService classifier)
    {
        _classifier = classifier;
    }

    public async Task<List<CacheTargetItem>> DiscoverCachesAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<CacheTargetItem>();
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            progress?.Report("Scanning package manager & build caches...");

            // 1. Known Cache Signatures in AppData\Local, AppData\Roaming, and UserProfile
            string[] rootsToSearch = [localAppData, roamingAppData, userProfile];
            foreach (var sig in _classifier.Rules.KnownCacheSignatures)
            {
                if (ct.IsCancellationRequested) break;
                foreach (var root in rootsToSearch)
                {
                    string candidate = Path.Combine(root, sig.NamePattern);
                    if (!string.IsNullOrWhiteSpace(sig.SubPath))
                    {
                        candidate = Path.Combine(candidate, sig.SubPath);
                    }

                    if (Directory.Exists(candidate))
                    {
                        long size = DiskScannerService.ComputeDirectorySize(candidate, followRootLink: false, ct);
                        if (size >= 5 * 1024 * 1024)
                        {
                            results.Add(new CacheTargetItem
                            {
                                Category = sig.Category,
                                Name = $"{sig.NamePattern} ({Path.GetFileName(root)})",
                                Description = sig.Description,
                                TargetPaths = [candidate],
                                SizeBytes = size,
                                IsSelected = true
                            });
                        }
                    }
                }
            }

            // 2. Discover Chromium-based Browser Caches across all profiles (Default, Profile *, Guest Profile, Snapshots)
            progress?.Report("Scanning installed web browser caches (safe mode)...");
            foreach (var browser in _classifier.Rules.BrowserRoots)
            {
                if (ct.IsCancellationRequested) break;
                string userDataPath = Path.Combine(localAppData, browser.RelativeUserDataPath);
                if (!Directory.Exists(userDataPath)) continue;

                var browserCacheDirs = new List<string>();

                try
                {
                    foreach (var subDir in Directory.GetDirectories(userDataPath))
                    {
                        string dirName = Path.GetFileName(subDir);
                        bool isProfile = string.Equals(dirName, "Default", StringComparison.OrdinalIgnoreCase) ||
                                         dirName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(dirName, "Guest Profile", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(dirName, "System Profile", StringComparison.OrdinalIgnoreCase);

                        if (isProfile)
                        {
                            foreach (var cacheRel in _classifier.Rules.ChromiumProfileCacheSubfolders)
                            {
                                string cPath = Path.Combine(subDir, cacheRel);
                                if (Directory.Exists(cPath))
                                {
                                    browserCacheDirs.Add(cPath);
                                }
                            }
                        }
                        else if (string.Equals(dirName, "Snapshots", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(dirName, "ShaderCache", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(dirName, "GrShaderCache", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(dirName, "component_crx_cache", StringComparison.OrdinalIgnoreCase))
                        {
                            browserCacheDirs.Add(subDir);
                        }
                    }
                }
                catch { }

                if (browserCacheDirs.Count > 0)
                {
                    long totalBrowserCache = browserCacheDirs.Sum(p => DiskScannerService.ComputeDirectorySize(p, false, ct));
                    if (totalBrowserCache >= 10 * 1024 * 1024)
                    {
                        results.Add(new CacheTargetItem
                        {
                            Category = "Web Browser Caches (Safe — Keeps Logins & History)",
                            Name = $"{browser.VendorName} Transient Caches",
                            Description = $"Cache, Code Cache, GPUCache, Service Worker Cache & Snapshots across {browserCacheDirs.Count} sub-paths",
                            TargetPaths = browserCacheDirs,
                            SizeBytes = totalBrowserCache,
                            IsSelected = true
                        });
                    }
                }
            }

            // 3. Mozilla Firefox Profile Caches
            string firefoxProfilesRoot = Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles");
            if (Directory.Exists(firefoxProfilesRoot))
            {
                var ffCacheDirs = new List<string>();
                try
                {
                    foreach (var prof in Directory.GetDirectories(firefoxProfilesRoot))
                    {
                        foreach (var sub in new[] { "cache2", "startupCache", "jumpListCache", "shader-cache" })
                        {
                            string p = Path.Combine(prof, sub);
                            if (Directory.Exists(p)) ffCacheDirs.Add(p);
                        }
                    }
                }
                catch { }

                long ffSize = ffCacheDirs.Sum(p => DiskScannerService.ComputeDirectorySize(p, false, ct));
                if (ffSize >= 10 * 1024 * 1024)
                {
                    results.Add(new CacheTargetItem
                    {
                        Category = "Web Browser Caches (Safe — Keeps Logins & History)",
                        Name = "Mozilla Firefox Profile Caches",
                        Description = "Firefox cache2, startupCache, and shader-cache folders",
                        TargetPaths = ffCacheDirs,
                        SizeBytes = ffSize,
                        IsSelected = true
                    });
                }
            }

            // 4. Universal Electron & Desktop App Cache Discovery in AppData\Roaming and AppData\Local
            progress?.Report("Scanning Electron & desktop application caches...");
            foreach (var appRoot in new[] { roamingAppData, localAppData })
            {
                if (!Directory.Exists(appRoot)) continue;
                string[] subDirs;
                try { subDirs = Directory.GetDirectories(appRoot); }
                catch { continue; }

                foreach (var appDir in subDirs)
                {
                    if (ct.IsCancellationRequested) break;
                    string appName = Path.GetFileName(appDir);
                    if (string.Equals(appName, "Microsoft", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(appName, "Google", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(appName, "BraveSoftware", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(appName, "Packages", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(appName, "Programs", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Only Electron-style "<app>-updater" staging folders (electron-updater). A bare "*Updater" suffix
                    // can match a live updater install (binaries + config), so it is no longer treated as a cache.
                    if (appName.EndsWith("-updater", StringComparison.OrdinalIgnoreCase))
                    {
                        // electron-updater keeps downloaded installers in "<app>-updater\pending"; purge only that when present.
                        string pendingDir = Path.Combine(appDir, "pending");
                        string updTarget = Directory.Exists(pendingDir) ? pendingDir : appDir;
                        long updSize = DiskScannerService.ComputeDirectorySize(updTarget, false, ct);
                        if (updSize >= 10 * 1024 * 1024)
                        {
                            results.Add(new CacheTargetItem
                            {
                                Category = "Updater & Temp Staging",
                                Name = $"{appName} Staging Cache",
                                Description = "Downloaded application updater installers (opt-in: check to purge)",
                                TargetPaths = [updTarget],
                                SizeBytes = updSize,
                                IsSelected = false
                            });
                        }
                        continue;
                    }

                    var electronCaches = new List<string>();
                    foreach (var eSub in _classifier.Rules.ElectronCacheSubfolders)
                    {
                        string candidate = Path.Combine(appDir, eSub);
                        if (Directory.Exists(candidate))
                        {
                            electronCaches.Add(candidate);
                        }
                    }

                    if (electronCaches.Count > 0)
                    {
                        long eSize = electronCaches.Sum(p => DiskScannerService.ComputeDirectorySize(p, false, ct));
                        if (eSize >= 15 * 1024 * 1024)
                        {
                            results.Add(new CacheTargetItem
                            {
                                Category = "Electron & IDE App Caches",
                                Name = $"{appName} ({Path.GetFileName(appRoot)}) Caches",
                                Description = $"Transient Cache, Code Cache, GPUCache & CachedData inside {appName}",
                                TargetPaths = electronCaches,
                                SizeBytes = eSize,
                                IsSelected = true
                            });
                        }
                    }
                }
            }

            // 5. RDP Terminal Server Client Cache & User Temp
            string rdpCache = Path.Combine(localAppData, "Microsoft", "Terminal Server Client", "Cache");
            if (Directory.Exists(rdpCache))
            {
                long rdpSize = DiskScannerService.ComputeDirectorySize(rdpCache, false, ct);
                if (rdpSize >= 10 * 1024 * 1024)
                {
                    results.Add(new CacheTargetItem
                    {
                        Category = "System & Diagnostics",
                        Name = "Remote Desktop (RDP) Bitmap Cache",
                        Description = "Cached bitmap tiles from Remote Desktop sessions",
                        TargetPaths = [rdpCache],
                        SizeBytes = rdpSize,
                        IsSelected = true
                    });
                }
            }

            string userTemp = Path.GetTempPath();
            if (Directory.Exists(userTemp))
            {
                long tempSize = DiskScannerService.ComputeDirectorySize(userTemp, false, ct);
                if (tempSize >= 10 * 1024 * 1024)
                {
                    results.Add(new CacheTargetItem
                    {
                        Category = "Updater & Temp Staging",
                        Name = "Windows User Temp (%TEMP%)",
                        Description = "Temporary files older than 60 minutes (skips active installer/session files). Opt-in: check to purge",
                        TargetPaths = [userTemp],
                        SizeBytes = tempSize,
                        IsSelected = false
                    });
                }
            }

            return results.OrderByDescending(r => r.SizeBytes).ToList();
        }, ct);
    }

    public async Task<(long BytesFreed, int FilesDeleted, int FilesSkipped)> PurgeSelectedCachesAsync(
        IEnumerable<CacheTargetItem> items,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        long totalFreed = 0;
        int deletedCount = 0;
        int skippedCount = 0;
        string tempRootNormalized = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');

        var selectedList = items.Where(i => i.IsSelected).ToList();

        await Task.Run(() =>
        {
            foreach (var item in selectedList)
            {
                if (ct.IsCancellationRequested) break;
                item.Status = "Cleaning...";
                progress?.Report($"Cleaning {item.Name}...");

                long itemFreed = 0;
                foreach (var targetPath in item.TargetPaths)
                {
                    if (!Directory.Exists(targetPath)) continue;
                    bool isTempDir = string.Equals(
                        Path.GetFullPath(targetPath).TrimEnd('\\'),
                        tempRootNormalized,
                        StringComparison.OrdinalIgnoreCase);

                    var (freed, del, skip) = CleanDirectoryContentsSafe(targetPath, skipRecentFiles: isTempDir, ct);
                    itemFreed += freed;
                    totalFreed += freed;
                    deletedCount += del;
                    skippedCount += skip;
                }

                long remaining = item.TargetPaths
                    .Where(Directory.Exists)
                    .Sum(p => DiskScannerService.ComputeDirectorySize(p, false, ct));

                item.SizeBytes = remaining;
                item.Status = $"Freed {itemFreed / 1048576.0:N1} MB";
                progress?.Report($"Finished {item.Name}: freed {itemFreed / 1048576.0:N1} MB.");
            }
        }, ct);

        return (totalFreed, deletedCount, skippedCount);
    }

    private static (long BytesFreed, int Deleted, int Skipped) CleanDirectoryContentsSafe(
        string rootDir,
        bool skipRecentFiles,
        CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0;
        int skipped = 0;
        DateTime cutoffUtc = DateTime.UtcNow.AddMinutes(-60);

        var dirInfo = new DirectoryInfo(rootDir);
        if (!dirInfo.Exists) return (0, 0, 0);

        var stack = new Stack<DirectoryInfo>();
        var allSubDirs = new List<DirectoryInfo>();
        stack.Push(dirInfo);

        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) break;
            var current = stack.Pop();

            try
            {
                foreach (var file in current.EnumerateFiles())
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        if (skipRecentFiles && file.LastWriteTimeUtc > cutoffUtc)
                        {
                            skipped++;
                            continue;
                        }

                        long len = file.Length;
                        file.Attributes = FileAttributes.Normal;
                        file.Delete();
                        freed += len;
                        deleted++;
                    }
                    catch
                    {
                        skipped++;
                    }
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
                            allSubDirs.Add(sub);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        for (int i = allSubDirs.Count - 1; i >= 0; i--)
        {
            try
            {
                allSubDirs[i].Delete(recursive: false);
            }
            catch { }
        }

        return (freed, deleted, skipped);
    }
}
