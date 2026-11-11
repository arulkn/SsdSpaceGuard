using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using SsdSpaceGuard.Models;

namespace SsdSpaceGuard.Services;

public class WslRelocatorService
{
    private readonly DiskScannerService _scanner;
    private const long SafetyBufferBytes = 1024L * 1024L * 1024L; // 1 GB safety buffer for VHDX moves

    public WslRelocatorService(DiskScannerService scanner)
    {
        _scanner = scanner;
    }

    /// <summary>
    /// Discovers registered WSL2 distributions via HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss
    /// and scans Packages/*/LocalState/*.vhdx. Docker Desktop distros are listed but never relocatable here.
    /// </summary>
    public async Task<List<VirtualDiskItem>> DiscoverVirtualDisksAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            progress?.Report("Discovering WSL2 distributions and .vhdx virtual disks...");
            var items = new List<VirtualDiskItem>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string osRoot = _scanner.GetOsDriveRoot();
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // 1. Inspect Registry HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss for exact WSL Distro -> BasePath mappings
            var registryDistros = GetWslDistrosFromRegistry();
            foreach (var (distroName, basePath) in registryDistros)
            {
                if (!Directory.Exists(basePath)) continue;
                try
                {
                    foreach (var vhdx in Directory.EnumerateFiles(basePath, "*.vhdx"))
                    {
                        var fi = new FileInfo(vhdx);
                        seenPaths.Add(Path.GetFullPath(vhdx));
                        bool onOs = string.Equals(Path.GetPathRoot(vhdx), osRoot, StringComparison.OrdinalIgnoreCase);
                        items.Add(new VirtualDiskItem
                        {
                            Name = Path.GetFileName(vhdx),
                            DistroOrOwner = distroName,
                            VhdxPath = vhdx,
                            SizeBytes = fi.Length,
                            IsWslDistro = !IsDockerDistro(distroName),
                            IsOnOsDrive = onOs,
                            Status = IsDockerDistro(distroName)
                                ? DockerGuidance
                                : (onOs ? "On OS Drive" : $"Offloaded ({Path.GetPathRoot(vhdx)})")
                        });
                    }
                }
                catch { }
            }

            // 2. Fallback scan of Packages/*/LocalState/*.vhdx if any wasn't in Lxss registry
            var cliDistros = GetRegisteredWslDistros();
            string packagesDir = Path.Combine(localAppData, "Packages");
            if (Directory.Exists(packagesDir))
            {
                try
                {
                    foreach (var pkgDir in Directory.GetDirectories(packagesDir))
                    {
                        string localState = Path.Combine(pkgDir, "LocalState");
                        if (!Directory.Exists(localState)) continue;

                        foreach (var vhdx in Directory.EnumerateFiles(localState, "*.vhdx"))
                        {
                            string full = Path.GetFullPath(vhdx);
                            if (seenPaths.Contains(full)) continue;
                            seenPaths.Add(full);

                            var fi = new FileInfo(vhdx);
                            string pkgName = Path.GetFileName(pkgDir);
                            string matchedDistro = MatchDistroName(pkgName, cliDistros);
                            bool onOs = string.Equals(Path.GetPathRoot(vhdx), osRoot, StringComparison.OrdinalIgnoreCase);

                            items.Add(new VirtualDiskItem
                            {
                                Name = Path.GetFileName(vhdx),
                                DistroOrOwner = !string.IsNullOrEmpty(matchedDistro) ? matchedDistro : pkgName,
                                VhdxPath = vhdx,
                                SizeBytes = fi.Length,
                                IsWslDistro = !string.IsNullOrEmpty(matchedDistro) && !IsDockerDistro(matchedDistro),
                                IsOnOsDrive = onOs,
                                Status = onOs ? "On OS Drive" : $"Offloaded ({Path.GetPathRoot(vhdx)})"
                            });
                        }
                    }
                }
                catch { }
            }

            return items.OrderByDescending(i => i.SizeBytes).ToList();
        }, ct);
    }

    /// <summary>
    /// Safely relocates a registered WSL2 distribution using Microsoft's official
    /// `wsl.exe --shutdown` + `wsl.exe --manage <Distro> --move <TargetDir>` command.
    /// Does NOT manually tamper with UWP package folders if `wsl --manage` is unsupported.
    /// </summary>
    public async Task<(bool Success, string Message)> RelocateVirtualDiskAsync(
        VirtualDiskItem vhd,
        string targetDriveRoot,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (IsDockerDistro(vhd.DistroOrOwner))
        {
            return (false, $"'{vhd.DistroOrOwner}' is managed by Docker Desktop. {DockerGuidance}");
        }

        if (!vhd.IsWslDistro)
        {
            return (false, $"'{vhd.DistroOrOwner}' is not a registered WSL2 distribution. Only registered WSL2 distros can be relocated via wsl --manage.");
        }

        var driveInfo = new DriveInfo(targetDriveRoot);
        if (!driveInfo.IsReady || driveInfo.DriveType != DriveType.Fixed ||
            !string.Equals(driveInfo.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Safety Block: Target drive '{targetDriveRoot}' must be an internal Fixed NTFS volume.");
        }

        if (driveInfo.AvailableFreeSpace < vhd.SizeBytes + SafetyBufferBytes)
        {
            return (false, $"Insufficient free space on {targetDriveRoot} (requires {(vhd.SizeBytes + SafetyBufferBytes) / 1073741824.0:N2} GB including 1 GB buffer).");
        }

        string safeName = string.Join("_", vhd.DistroOrOwner.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        string targetDir = Path.Combine(targetDriveRoot, "WSL_Offload", safeName);

        return await Task.Run(() =>
        {
            try
            {
                vhd.Status = "Shutting down WSL...";
                progress?.Report("Running 'wsl.exe --shutdown' to cleanly unmount all WSL2 virtual disks...");
                RunProcess("wsl.exe", "--shutdown", out _);

                Directory.CreateDirectory(targetDir);

                vhd.Status = "Moving via wsl --manage...";
                progress?.Report($"Relocating WSL2 distro '{vhd.DistroOrOwner}' to '{targetDir}' via 'wsl --manage --move'...");
                int code = RunProcess("wsl.exe", $"--manage \"{vhd.DistroOrOwner}\" --move \"{targetDir}\"", out string wslOut);

                string expectedNewVhdx = Path.Combine(targetDir, vhd.Name);
                if (code == 0 || File.Exists(expectedNewVhdx))
                {
                    vhd.VhdxPath = expectedNewVhdx;
                    vhd.IsOnOsDrive = false;
                    vhd.Status = $"Moved -> {targetDir}";
                    return (true, $"Successfully relocated WSL2 distro '{vhd.DistroOrOwner}' to '{targetDir}'.");
                }

                vhd.Status = "Move Rejected by WSL";
                return (false, $"wsl.exe --manage returned code {code}: {wslOut}. Original WSL2 virtual disk was left untouched.");
            }
            catch (Exception ex)
            {
                vhd.Status = "Error";
                return (false, $"Failed to relocate WSL2 virtual disk: {ex.Message}");
            }
        }, ct);
    }

    private const string DockerGuidance =
        "Docker Desktop disk — move it via Docker Desktop -> Settings -> Resources -> Advanced -> Disk image location (wsl --manage is not supported for Docker's distros).";

    private static bool IsDockerDistro(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);

    private static List<(string DistroName, string BasePath)> GetWslDistrosFromRegistry()
    {
        var result = new List<(string, string)>();
        try
        {
            using var lxssKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxssKey == null) return result;

            foreach (var subKeyName in lxssKey.GetSubKeyNames())
            {
                using var distroKey = lxssKey.OpenSubKey(subKeyName);
                if (distroKey == null) continue;

                string? distroName = distroKey.GetValue("DistributionName") as string;
                string? basePath = distroKey.GetValue("BasePath") as string;
                if (!string.IsNullOrWhiteSpace(distroName) && !string.IsNullOrWhiteSpace(basePath))
                {
                    // Strip extended-length path prefix "\\?\" if present in WSL registry
                    if (basePath.StartsWith(@"\\?\"))
                    {
                        basePath = basePath.Substring(4);
                    }
                    result.Add((distroName, basePath));
                }
            }
        }
        catch { }
        return result;
    }

    private static List<string> GetRegisteredWslDistros()
    {
        var list = new List<string>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "--list --quiet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.Unicode,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string raw = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(4000);
                foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    string clean = line.Replace("\0", "").Trim();
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        list.Add(clean);
                    }
                }
            }
        }
        catch { }
        return list;
    }

    private static string MatchDistroName(string packageFolderName, List<string> registeredDistros)
    {
        foreach (var d in registeredDistros)
        {
            if (packageFolderName.Contains(d, StringComparison.OrdinalIgnoreCase))
            {
                return d;
            }
        }
        if (packageFolderName.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase))
        {
            return registeredDistros.FirstOrDefault(d => d.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase)) ?? "Ubuntu";
        }
        return string.Empty;
    }

    private static int RunProcess(string fileName, string args, out string output)
    {
        output = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // wsl.exe writes UTF-16LE by default; decode both streams accordingly
                StandardOutputEncoding = Encoding.Unicode,
                StandardErrorEncoding = Encoding.Unicode,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            // Strip stray NULs in case WSL_UTF8=1 makes wsl.exe emit UTF-8 instead
            output = (o + " " + e).Replace("\0", "").Trim();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }
}
