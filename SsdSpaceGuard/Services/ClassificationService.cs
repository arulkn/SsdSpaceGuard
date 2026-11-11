using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using SsdSpaceGuard.Models;

namespace SsdSpaceGuard.Services;

public class ClassificationService
{
    public RuleConfig Rules { get; private set; } = new();
    public bool RulesHealthy { get; private set; } = true;
    public string? RulesLoadError { get; private set; }

    public string ActiveRulesPath { get; private set; } = string.Empty;
    public string DefaultTemplateRulesPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "rules.json");

    private static readonly HashSet<string> ProtectedUserShellFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData",
        "Application Data",
        "Cookies",
        "Desktop",
        "Documents",
        "My Documents",
        "Downloads",
        "Favorites",
        "Links",
        "Local Settings",
        "Music",
        "NetHood",
        "OneDrive",
        "Pictures",
        "PrintHood",
        "Recent",
        "Saved Games",
        "Searches",
        "SendTo",
        "Start Menu",
        "Templates",
        "Videos"
    };

    public ClassificationService()
    {
        InitializeRulesStorage();
        LoadRules();
    }

    private void InitializeRulesStorage()
    {
        try
        {
            string userConfigDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SsdSpaceGuard");
            Directory.CreateDirectory(userConfigDir);
            string userRulesPath = Path.Combine(userConfigDir, "rules.json");

            if (!File.Exists(userRulesPath) && File.Exists(DefaultTemplateRulesPath))
            {
                File.Copy(DefaultTemplateRulesPath, userRulesPath, overwrite: false);
            }

            ActiveRulesPath = File.Exists(userRulesPath) ? userRulesPath : DefaultTemplateRulesPath;
        }
        catch
        {
            ActiveRulesPath = DefaultTemplateRulesPath;
        }
    }

    public static bool TryValidateRulesJson(string json, out RuleConfig? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Rules JSON is empty.";
            return false;
        }

        try
        {
            var cfg = JsonSerializer.Deserialize<RuleConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (cfg == null)
            {
                error = "Rules JSON deserialized to null.";
                return false;
            }

            if (cfg.ProtectedFolderPatterns == null || cfg.ProtectedFolderPatterns.Count == 0)
            {
                error = "Rules JSON must contain a non-empty 'ProtectedFolderPatterns' array.";
                return false;
            }

            parsed = cfg;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Invalid JSON syntax: {ex.Message}";
            return false;
        }
    }

    public void LoadRules()
    {
        try
        {
            string pathToRead = File.Exists(ActiveRulesPath) ? ActiveRulesPath : DefaultTemplateRulesPath;
            if (!File.Exists(pathToRead))
            {
                RulesHealthy = false;
                RulesLoadError = $"Rules file not found at '{pathToRead}'.";
                return;
            }

            string json = File.ReadAllText(pathToRead);
            if (TryValidateRulesJson(json, out var parsed, out string? err) && parsed != null)
            {
                Rules = parsed;
                RulesHealthy = true;
                RulesLoadError = null;
            }
            else
            {
                RulesHealthy = false;
                RulesLoadError = err ?? "Malformed rules.json.";
            }
        }
        catch (Exception ex)
        {
            RulesHealthy = false;
            RulesLoadError = ex.Message;
        }
    }

    public (FolderClassification Classification, string Reason) ClassifyDirectory(
        string fullPath,
        bool isJunction,
        string? junctionTarget)
    {
        // Fail-closed if rules.json failed to load or is corrupt
        if (!RulesHealthy)
        {
            return (FolderClassification.SystemProtected,
                $"Safety Lock: rules.json failed to load ({RulesLoadError}). Fix or reload rules in Tab 4 before offloading.");
        }

        // IMPORTANT: All OS-protection checks run BEFORE the junction check.
        // Windows ships its own compatibility junctions inside the user profile (e.g. "My Documents" -> Documents,
        // "Application Data" -> AppData\Roaming, AppData\Local\"Application Data" -> AppData\Local). If those were
        // classified as AlreadyOffloadedJunction, the Restore action would copy and then DELETE their real targets.
        string normalized = Path.GetFullPath(fullPath).TrimEnd('\\');
        string folderName = Path.GetFileName(normalized);
        string? rootDrive = Path.GetPathRoot(normalized)?.TrimEnd('\\');

        // 0. Block drive roots, Windows system directories, Program Files, ProgramData, Users root, and current UserProfile root
        if (string.Equals(normalized, rootDrive, StringComparison.OrdinalIgnoreCase))
        {
            return (FolderClassification.SystemProtected, "Drive root cannot be offloaded.");
        }

        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\');
        string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\');
        string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd('\\');
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        string? usersParent = Path.GetDirectoryName(userProfile)?.TrimEnd('\\');
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\');
        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).TrimEnd('\\');

        string[] criticalRoots = [windowsDir, progFiles, progFilesX86, progData];
        foreach (var crit in criticalRoots)
        {
            if (!string.IsNullOrEmpty(crit) &&
                (string.Equals(normalized, crit, StringComparison.OrdinalIgnoreCase) ||
                 normalized.StartsWith(crit + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                return (FolderClassification.SystemProtected,
                    "Critical Windows OS or Program Files directory. Offloading will corrupt Windows or break servicing.");
            }
        }

        if (string.Equals(normalized, userProfile, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, usersParent, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, localAppData, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, roamingAppData, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, Path.Combine(localAppData, "Programs"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, Path.Combine(localAppData, "Packages"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, Path.Combine(localAppData, "Microsoft"), StringComparison.OrdinalIgnoreCase))
        {
            return (FolderClassification.SystemProtected,
                "Core user profile container root. Offload individual application subfolders instead.");
        }

        // Protect AppData\Roaming\Microsoft and all its subdirectories (holds DPAPI Protect, Credentials, Crypto, Certificates, Start Menu)
        string roamingMicrosoft = Path.Combine(roamingAppData, "Microsoft");
        if (string.Equals(normalized, roamingMicrosoft, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(roamingMicrosoft + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return (FolderClassification.SystemProtected,
                "AppData\\Roaming\\Microsoft contains Windows DPAPI encryption keys, certificates, credentials, and Start Menu items. Locked on OS drive.");
        }

        // App's own data folder (holds offload_manifest.json and user rules.json)
        string ownAppData = Path.Combine(localAppData, "SsdSpaceGuard");
        if (string.Equals(normalized, ownAppData, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(ownAppData + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return (FolderClassification.SystemProtected,
                "SSD Space Guard's own configuration & offload manifest folder. Kept on the OS drive.");
        }

        // 1. Protect Windows Shell Known Folders (and their legacy compatibility junctions) inside UserProfile
        string? parentDir = Path.GetDirectoryName(normalized)?.TrimEnd('\\');
        if (string.Equals(parentDir, userProfile, StringComparison.OrdinalIgnoreCase) &&
            ProtectedUserShellFolders.Contains(folderName))
        {
            return (FolderClassification.SystemProtected,
                "Windows Shell Known Folder. Use Windows Properties -> 'Location' tab (not raw NTFS junctions) to relocate Shell folders safely.");
        }

        // 2. Protect ALL UWP / MSIX AppContainer folders inside AppData\Local\Packages
        string packagesRoot = Path.Combine(localAppData, "Packages");
        if (normalized.StartsWith(packagesRoot + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return (FolderClassification.SystemProtected,
                "UWP / MSIX AppContainer package directory. Junctioning breaks Windows Store AppContainer sandbox permissions; use Windows Settings -> Installed Apps -> Move instead.");
        }

        // 3. Protect AppData\Local\Temp from being junctioned (Windows Installer / MSI security blocks temp directory junctions)
        if (string.Equals(normalized, Path.Combine(localAppData, "Temp"), StringComparison.OrdinalIgnoreCase))
        {
            return (FolderClassification.SystemProtected,
                "Windows User Temp (%TEMP%). Safe to clean in Tab 2, but must NEVER be junctioned because MSI installers block reparse points on Temp.");
        }

        // 4. Check Configurable Protected Patterns from rules.json
        foreach (var pattern in Rules.ProtectedFolderPatterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern) && normalized.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return (FolderClassification.SystemProtected,
                    $"Matches protected OS/system pattern '{pattern}'. Locked on OS drive to prevent Windows corruption.");
            }
        }

        // 5. Existing junctions / reparse points. Only junctions pointing to a DIFFERENT drive can be offloads
        // made by this app (offload refuses same-drive targets). Same-drive or unresolvable links belong to
        // Windows or another tool and must never be offered for Restore.
        if (isJunction)
        {
            if (string.IsNullOrWhiteSpace(junctionTarget) || !Path.IsPathFullyQualified(junctionTarget))
            {
                return (FolderClassification.SystemProtected,
                    "Reparse point with an unknown or relative target (not created by SSD Space Guard). Left untouched.");
            }

            string? targetRoot = Path.GetPathRoot(Path.GetFullPath(junctionTarget))?.TrimEnd('\\');
            if (string.Equals(targetRoot, rootDrive, StringComparison.OrdinalIgnoreCase))
            {
                return (FolderClassification.SystemProtected,
                    $"Same-drive link -> {junctionTarget}. This is a Windows compatibility junction or a link made by another tool, not an SSD Space Guard offload. Left untouched.");
            }

            return (FolderClassification.AlreadyOffloadedJunction,
                $"Already offloaded via NTFS Junction -> {junctionTarget}");
        }

        // 6. Check if directory contains a WSL2 / Hyper-V Virtual Disk (.vhdx)
        try
        {
            string localState = Path.Combine(normalized, "LocalState");
            if (Directory.Exists(localState) && Directory.EnumerateFiles(localState, "*.vhdx").Any())
            {
                return (FolderClassification.VirtualDiskContainer,
                    "Contains WSL2/Hyper-V virtual disk (ext4.vhdx). Use the 'WSL2 & Virtual Disks' tab to relocate via wsl --manage.");
            }
            if (Directory.EnumerateFiles(normalized, "*.vhdx", SearchOption.TopDirectoryOnly).Any())
            {
                return (FolderClassification.VirtualDiskContainer,
                    "Contains virtual disk (.vhdx). Use the 'WSL2 & Virtual Disks' tab for safe offline relocation.");
            }
        }
        catch { }

        // 7. Check Known Cache Signatures
        foreach (var sig in Rules.KnownCacheSignatures)
        {
            if (string.Equals(folderName, sig.NamePattern, StringComparison.OrdinalIgnoreCase))
            {
                if (sig.RecommendJunction)
                {
                    return (FolderClassification.SafeCachePurge,
                        $"{sig.Description} — Safe to purge in Tab 2 AND safe to Junction-offload so future cache growth stays off OS SSD.");
                }
                else
                {
                    return (FolderClassification.SystemProtected,
                        $"{sig.Description} — Safe to purge in Tab 2, but kept physically on OS drive.");
                }
            }
        }

        if (folderName.EndsWith("-updater", StringComparison.OrdinalIgnoreCase))
        {
            return (FolderClassification.SafeCachePurge,
                "Application updater staging folder — can be cleaned in Tab 2.");
        }

        // 8. Check Good Offload Hints (AI models, SDKs, heavy standalone app data)
        foreach (var hint in Rules.GoodOffloadHints)
        {
            if (folderName.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return (FolderClassification.RecommendedOffload,
                    $"Heavy application/SDK/model directory matching '{hint}'. Verified safe for NTFS Junction offload when app is closed.");
            }
        }

        // 9. Default generic classification
        return (FolderClassification.GeneralAppData,
            "Standard application directory. Safe to offload via NTFS Junction when its owning application is closed.");
    }

    /// <summary>
    /// Detects any running processes locking targetDir using:
    /// 1. Native Windows Restart Manager API (rstrtmgr.dll RmGetList) on sampled files inside targetDir
    ///    to detect open file handles held by ANY process.
    /// 2. Process executable path and name inspection (Process.GetProcesses()).
    /// </summary>
    public List<string> FindLockingProcesses(string targetDir)
    {
        var locking = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string normalizedTarget = Path.GetFullPath(targetDir).TrimEnd('\\') + "\\";
        string folderToken = Path.GetFileName(targetDir.TrimEnd('\\'));

        // 1. Query Windows Restart Manager API for open file handles inside targetDir
        try
        {
            var candidateFiles = SampleFilesForRestartManager(targetDir, maxFiles: 200);
            if (candidateFiles.Count > 0)
            {
                foreach (var holder in QueryRestartManagerForFiles(candidateFiles))
                {
                    locking.Add(holder);
                }
            }
        }
        catch { }

        // 2. Check running process executable paths & matching process names
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id <= 4) continue;
                string? exePath = null;
                try
                {
                    exePath = proc.MainModule?.FileName;
                }
                catch { }

                if (!string.IsNullOrEmpty(exePath) &&
                    exePath.StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    locking.Add($"{proc.ProcessName} (PID {proc.Id})");
                    continue;
                }

                if (folderToken.Length >= 4 &&
                    (proc.ProcessName.Equals(folderToken, StringComparison.OrdinalIgnoreCase) ||
                     (folderToken.Contains(proc.ProcessName, StringComparison.OrdinalIgnoreCase) && proc.ProcessName.Length >= 5)))
                {
                    locking.Add($"{proc.ProcessName} (PID {proc.Id})");
                }
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }

        return locking.OrderBy(x => x).ToList();
    }

    private static List<string> SampleFilesForRestartManager(string rootDir, int maxFiles)
    {
        var files = new List<string>(maxFiles);
        var stack = new Stack<DirectoryInfo>();
        try
        {
            var root = new DirectoryInfo(rootDir);
            if (!root.Exists) return files;
            stack.Push(root);
        }
        catch { return files; }

        while (stack.Count > 0 && files.Count < maxFiles)
        {
            var cur = stack.Pop();
            try
            {
                foreach (var fi in cur.EnumerateFiles())
                {
                    files.Add(fi.FullName);
                    if (files.Count >= maxFiles) break;
                }
            }
            catch { }

            if (files.Count >= maxFiles) break;

            try
            {
                foreach (var sub in cur.EnumerateDirectories())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        stack.Push(sub);
                    }
                }
            }
            catch { }
        }

        return files;
    }

    #region Win32 Restart Manager Interop (rstrtmgr.dll)

    private const int RmRebootReasonNone = 0;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;
    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[]? rgsFilenames,
        uint nApplications,
        [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    private static List<string> QueryRestartManagerForFiles(List<string> filePaths)
    {
        var holders = new List<string>();
        string sessionKey = Guid.NewGuid().ToString("N");
        if (RmStartSession(out uint handle, 0, sessionKey) != 0)
        {
            return holders;
        }

        try
        {
            string[] resources = filePaths.ToArray();
            if (RmRegisterResources(handle, (uint)resources.Length, resources, 0, null, 0, null) != 0)
            {
                return holders;
            }

            uint needed = 0;
            uint count = 0;
            uint rebootReasons = RmRebootReasonNone;
            int res = RmGetList(handle, out needed, ref count, null, ref rebootReasons);
            if (res == ERROR_MORE_DATA && needed > 0)
            {
                var procInfos = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(handle, out needed, ref count, procInfos, ref rebootReasons) == 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        string appName = string.IsNullOrWhiteSpace(procInfos[i].strAppName)
                            ? "Process"
                            : procInfos[i].strAppName;
                        holders.Add($"{appName} (PID {procInfos[i].Process.dwProcessId})");
                    }
                }
            }
        }
        finally
        {
            RmEndSession(handle);
        }

        return holders;
    }

    #endregion
}
