using System.Diagnostics;
using System.IO;
using SsdSpaceGuard.Models;

namespace SsdSpaceGuard.Services;

public class JunctionMigrationService
{
    private readonly ClassificationService _classifier;
    private readonly OffloadManifestService _manifest;
    private const long SafetyBufferBytes = 512L * 1024L * 1024L; // 512 MB minimum free space buffer

    public JunctionMigrationService(ClassificationService classifier, OffloadManifestService? manifest = null)
    {
        _classifier = classifier;
        _manifest = manifest ?? new OffloadManifestService();
    }

    /// <summary>
    /// Performs a hardened 6-step transactional offload of a directory to targetBaseRoot and replaces
    /// the original directory with an NTFS Directory Junction (mklink /J):
    /// 1. Re-validate OS safety rules, fixed NTFS target drive requirement, non-empty destination collision check, and free space buffer.
    /// 2. Check active process locks (via Win32 Restart Manager RmGetList + Process paths).
    /// 3. Copy all files/subdirectories to target destination preserving timestamps & ACLs, cleaning up partial target on cancel/error.
    /// 4. Atomically rename source directory to <Name>._ssdguard_backup and verify backup-vs-destination tree parity (catching any concurrent writes).
    /// 5. Create NTFS Junction at original path pointing to destination & verify ReparsePoint.
    /// 6. Record ownership in offload_manifest.json and delete ._ssdguard_backup (or auto-rollback on any error).
    /// </summary>
    public async Task<(bool Success, string Message)> OffloadFolderWithJunctionAsync(
        ScannedFolderItem item,
        string targetBaseRoot,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        string sourcePath = Path.GetFullPath(item.FullPath).TrimEnd('\\');
        if (!Directory.Exists(sourcePath))
        {
            return (false, $"Source folder does not exist: {sourcePath}");
        }

        // Re-verify classification at execution time as a hard guardrail
        var (liveClass, liveReason) = _classifier.ClassifyDirectory(sourcePath, item.IsJunction, item.JunctionTarget);
        if (liveClass == FolderClassification.SystemProtected || liveClass == FolderClassification.VirtualDiskContainer)
        {
            return (false, $"Safety Block: '{item.Name}' cannot be offloaded via NTFS Junction. {liveReason}");
        }

        if (item.IsJunction)
        {
            return (false, $"'{item.Name}' is already an NTFS Junction pointing to {item.JunctionTarget}.");
        }

        // Validate target drive is a ready Fixed NTFS volume on a different drive with sufficient free space
        string? srcDriveRoot = Path.GetPathRoot(sourcePath);
        string? dstDriveRoot = Path.GetPathRoot(Path.GetFullPath(targetBaseRoot));
        if (string.IsNullOrWhiteSpace(dstDriveRoot))
        {
            return (false, $"Invalid target path: {targetBaseRoot}");
        }

        if (string.Equals(srcDriveRoot, dstDriveRoot, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Target path ({targetBaseRoot}) is on the same drive ({srcDriveRoot}) as the source folder. Select a different drive.");
        }

        var dstDriveInfo = new DriveInfo(dstDriveRoot);
        if (!dstDriveInfo.IsReady || dstDriveInfo.DriveType != DriveType.Fixed)
        {
            return (false, $"Safety Block: Target drive '{dstDriveRoot}' must be an internal Fixed disk (not a removable USB or network drive that could be disconnected).");
        }

        if (!string.Equals(dstDriveInfo.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Safety Block: Target drive '{dstDriveRoot}' is formatted as {dstDriveInfo.DriveFormat}. Only NTFS volumes are supported for AppData junctions.");
        }

        if (dstDriveInfo.AvailableFreeSpace < item.SizeBytes + SafetyBufferBytes)
        {
            return (false, $"Insufficient space on {dstDriveRoot}: needs {(item.SizeBytes + SafetyBufferBytes) / 1073741824.0:N2} GB free (including 512 MB safety buffer), but only {dstDriveInfo.AvailableFreeSpace / 1073741824.0:N2} GB is available.");
        }

        // Check for running processes / open file handles using this folder
        var lockingProcs = _classifier.FindLockingProcesses(sourcePath);
        if (lockingProcs.Count > 0)
        {
            return (false, $"Active process(es) or open file handles detected for '{item.Name}': {string.Join(", ", lockingProcs)}. Please close them before offloading.");
        }

        string safeScope = string.Join("_", item.ScopeCategory.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safeScope)) safeScope = "Offloaded";

        string destPath = Path.Combine(targetBaseRoot, safeScope, item.Name);
        string backupPath = sourcePath + "._ssdguard_backup";

        return await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(backupPath))
                {
                    return (false, $"A temporary backup folder already exists at '{backupPath}'. Please inspect it first.");
                }

                // Prevent destination collisions (e.g. two custom folders with the same leaf name, or leftover files)
                if (Directory.Exists(destPath) && Directory.EnumerateFileSystemEntries(destPath).Any())
                {
                    return (false, $"Destination folder '{destPath}' already exists and is not empty (possible folder collision). Choose another Offload Folder or remove the existing target folder first.");
                }

                item.StatusText = "Copying to target drive...";
                progress?.Report($"[1/5] Copying '{item.Name}' ({item.SizeFormatted}) -> '{destPath}'...");

                Directory.CreateDirectory(destPath);

                // Copy recursively and verify every file succeeded
                var (copiedFiles, copiedBytes, errorMsg) = CopyDirectoryVerified(sourcePath, destPath, progress, item.Name, item.SizeBytes, ct);
                if (ct.IsCancellationRequested || errorMsg != null)
                {
                    // Clean up partial copy on target drive since destPath was newly created/empty
                    DeleteDirectoryForce(destPath);
                    if (ct.IsCancellationRequested)
                    {
                        item.StatusText = "Cancelled";
                        return (false, $"Offload of '{item.Name}' was cancelled. Partial copy on target drive was cleaned up; original folder on OS drive is untouched.");
                    }

                    item.StatusText = "Copy Aborted";
                    return (false, $"Safety Abort while copying '{item.Name}': {errorMsg}. Partial copy on target drive was cleaned up; original folder on OS drive is untouched.");
                }

                // Step 2: Rename original directory to backup (freezes the source tree against further writes by path)
                item.StatusText = "Creating atomic backup...";
                progress?.Report($"[2/5] Renaming '{sourcePath}' to backup before verifying tree parity...");
                try
                {
                    Directory.Move(sourcePath, backupPath);
                }
                catch (Exception moveEx)
                {
                    DeleteDirectoryForce(destPath);
                    item.StatusText = "Locked by process";
                    return (false, $"Could not rename '{item.Name}' because a process still has an open handle inside it ({moveEx.Message}). Partial target copy was cleaned up; original folder remains intact on OS drive.");
                }

                // Step 3: Verify backup-vs-destination parity (ensures no files were added/modified during the copy window)
                item.StatusText = "Verifying copy parity...";
                progress?.Report($"[3/5] Verifying 100% file count, size & timestamp parity between backup and destination...");
                string? diffError = VerifyDirectoryTreesMatch(backupPath, destPath);
                if (diffError != null)
                {
                    bool rolledBack = TryRollbackBackup(backupPath, sourcePath);
                    DeleteDirectoryForce(destPath);
                    item.StatusText = rolledBack ? "Rolled back (concurrent write)" : "Rollback Error";
                    return (false, rolledBack
                        ? $"Safety Abort: Files inside '{item.Name}' changed while copying ({diffError}). Original folder was safely restored on OS drive; close background processes and retry."
                        : $"Safety Abort: Files changed while copying ({diffError}), and automatic rename back to '{sourcePath}' failed. Your original files are intact at '{backupPath}'.");
                }

                // Step 4: Create NTFS Directory Junction using cmd /c mklink /J
                item.StatusText = "Creating NTFS Junction...";
                progress?.Report($"[4/5] Creating NTFS Junction '{sourcePath}' -> '{destPath}'...");

                bool junctionCreated = CreateNtfsJunction(sourcePath, destPath, out string mklinkOutput);
                var (isReparse, linkTarget) = DiskScannerService.InspectReparsePoint(sourcePath);

                if (!junctionCreated || !isReparse)
                {
                    progress?.Report($"Junction creation failed ({mklinkOutput}). Rolling back backup to original location...");
                    bool rolledBack = TryRollbackBackup(backupPath, sourcePath);
                    DeleteDirectoryForce(destPath);
                    item.StatusText = rolledBack ? "Rolled back" : "Rollback Error";
                    return (false, rolledBack
                        ? $"Failed to create NTFS Junction: {mklinkOutput}. Original folder was safely restored."
                        : $"Failed to create NTFS Junction ({mklinkOutput}) and could not rename backup back to '{sourcePath}'. Your original files are intact at '{backupPath}'.");
                }

                // Step 5: Record ownership BEFORE deleting the backup so Restore can always prove this junction is ours.
                item.StatusText = "Reclaiming OS SSD space...";
                progress?.Report($"[5/5] Junction verified! Recording manifest and deleting backup on OS drive to free {item.SizeFormatted}...");
                _manifest.Record(sourcePath, destPath);
                DeleteDirectoryForce(backupPath);

                item.IsJunction = true;
                item.JunctionTarget = linkTarget ?? destPath;
                item.Classification = FolderClassification.AlreadyOffloadedJunction;
                item.StatusText = "Offloaded";

                return (true, $"Successfully offloaded '{item.Name}' ({copiedFiles:N0} files, {copiedBytes / 1048576.0:N1} MB) -> '{destPath}'.");
            }
            catch (Exception ex)
            {
                bool rolledBack = TryRollbackBackup(backupPath, sourcePath);
                item.StatusText = "Error";
                return (false, rolledBack
                    ? $"Error offloading '{item.Name}': {ex.Message}. Original folder is intact at '{sourcePath}'."
                    : $"Error offloading '{item.Name}': {ex.Message}. Check '{backupPath}' and '{destPath}'.");
            }
        }, ct);
    }

    private static bool TryRollbackBackup(string backupPath, string sourcePath)
    {
        try
        {
            if (!Directory.Exists(backupPath))
            {
                return Directory.Exists(sourcePath);
            }

            if (Directory.Exists(sourcePath))
            {
                var (isReparse, _) = DiskScannerService.InspectReparsePoint(sourcePath);
                if (isReparse)
                {
                    Directory.Delete(sourcePath, recursive: false);
                }
            }

            if (!Directory.Exists(sourcePath))
            {
                Directory.Move(backupPath, sourcePath);
            }

            return Directory.Exists(sourcePath) && !Directory.Exists(backupPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Safely restores an NTFS Junction folder back to its physical location on the OS drive
    /// using a staging directory so cancelling mid-restore never breaks the existing junction.
    /// SAFETY: Restore finishes by DELETING the junction's target folder, so it only runs for junctions this
    /// app created (recorded in the offload manifest, or legacy offloads under the configured offload root).
    /// </summary>
    public async Task<(bool Success, string Message)> RestoreJunctionToOriginalDriveAsync(
        ScannedFolderItem item,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        string? offloadRoot = null)
    {
        string junctionPath = Path.GetFullPath(item.FullPath).TrimEnd('\\');

        // Re-inspect the junction live; never trust scan-time data for a destructive operation.
        var (isReparse, liveTarget) = DiskScannerService.InspectReparsePoint(junctionPath);
        if (!isReparse || string.IsNullOrWhiteSpace(liveTarget) || !Path.IsPathFullyQualified(liveTarget))
        {
            return (false, $"'{item.Name}' is not an active NTFS Junction with a resolvable target.");
        }

        var (liveClass, liveReason) = _classifier.ClassifyDirectory(junctionPath, true, liveTarget);
        if (liveClass != FolderClassification.AlreadyOffloadedJunction)
        {
            return (false, $"Safety Block: '{item.Name}' cannot be restored. {liveReason}");
        }

        string targetDataPath = Path.GetFullPath(liveTarget).TrimEnd('\\');

        string? ownershipError = ValidateRestoreOwnership(junctionPath, targetDataPath, offloadRoot);
        if (ownershipError != null)
        {
            return (false, $"Safety Block: {ownershipError}");
        }

        if (!Directory.Exists(targetDataPath))
        {
            return (false, $"Target directory '{targetDataPath}' could not be found.");
        }

        string? osDriveRoot = Path.GetPathRoot(junctionPath);
        if (!string.IsNullOrEmpty(osDriveRoot))
        {
            var osDriveInfo = new DriveInfo(osDriveRoot);
            if (osDriveInfo.IsReady && osDriveInfo.AvailableFreeSpace < item.SizeBytes + SafetyBufferBytes)
            {
                return (false, $"Insufficient free space on {osDriveRoot} to restore '{item.Name}' ({item.SizeFormatted}). Needs at least {(item.SizeBytes + SafetyBufferBytes) / 1073741824.0:N2} GB free.");
            }
        }

        var lockingProcs = _classifier.FindLockingProcesses(junctionPath);
        if (lockingProcs.Count > 0)
        {
            return (false, $"Active process(es) detected for '{item.Name}': {string.Join(", ", lockingProcs)}. Please close them first.");
        }

        string stagingPath = junctionPath + "._restore_staging";

        return await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(stagingPath))
                {
                    DeleteDirectoryForce(stagingPath);
                }

                item.StatusText = "Staging restore on OS drive...";
                progress?.Report($"[1/3] Copying data from '{targetDataPath}' to staging folder '{stagingPath}'...");
                Directory.CreateDirectory(stagingPath);

                var (_, _, errorMsg) = CopyDirectoryVerified(targetDataPath, stagingPath, progress, item.Name, item.SizeBytes, ct);
                if (ct.IsCancellationRequested || errorMsg != null)
                {
                    DeleteDirectoryForce(stagingPath);
                    item.StatusText = "Restore aborted (Junction intact)";
                    return (false, errorMsg ?? "Restore was cancelled. Existing NTFS Junction remains intact.");
                }

                string? diffErr = VerifyDirectoryTreesMatch(targetDataPath, stagingPath);
                if (diffErr != null)
                {
                    DeleteDirectoryForce(stagingPath);
                    item.StatusText = "Restore aborted (Junction intact)";
                    return (false, $"Safety Abort: Files in '{targetDataPath}' changed during restore ({diffErr}). Existing NTFS Junction remains intact.");
                }

                // Step 2: Remove NTFS Junction (ReparsePoint only) and swap in staging folder
                item.StatusText = "Swapping restored folder...";
                progress?.Report($"[2/3] Removing NTFS Junction and activating restored folder at '{junctionPath}'...");
                Directory.Delete(junctionPath, recursive: false);
                try
                {
                    Directory.Move(stagingPath, junctionPath);
                }
                catch (Exception moveEx)
                {
                    // Roll back: put the junction back so the app keeps working from the secondary drive.
                    bool relinked = !Directory.Exists(junctionPath) && CreateNtfsJunction(junctionPath, targetDataPath, out _);
                    if (relinked)
                    {
                        DeleteDirectoryForce(stagingPath);
                        item.StatusText = "Restore aborted (Junction re-created)";
                        return (false, $"Could not activate the restored folder ({moveEx.Message}). The original junction was re-created; data on '{targetDataPath}' is untouched.");
                    }

                    item.StatusText = "Restore Error — manual check needed";
                    return (false, $"Could not activate the restored folder ({moveEx.Message}) and could not re-create the junction. Your data is intact at '{targetDataPath}' and in '{stagingPath}'. To undo manually run: mklink /J \"{junctionPath}\" \"{targetDataPath}\"");
                }

                // Step 3: Clean up offloaded copy on secondary drive — only once a real (non-link) folder is in place.
                var (stillReparse, _) = DiskScannerService.InspectReparsePoint(junctionPath);
                if (!Directory.Exists(junctionPath) || stillReparse)
                {
                    item.StatusText = "Restore Error — manual check needed";
                    return (false, $"Restored folder could not be verified at '{junctionPath}'. The offloaded copy at '{targetDataPath}' was NOT deleted.");
                }

                // Final parity check now that the junction is gone: nothing can reach targetDataPath through the old
                // path any more, so any difference here means a write landed between the first check and the swap.
                string? postSwapDiff = VerifyDirectoryTreesMatch(targetDataPath, junctionPath);
                if (postSwapDiff != null)
                {
                    // Undo the swap: put the restored copy aside and re-create the junction to the (newer) target data.
                    try
                    {
                        Directory.Move(junctionPath, stagingPath);
                        if (CreateNtfsJunction(junctionPath, targetDataPath, out _))
                        {
                            DeleteDirectoryForce(stagingPath);
                            item.StatusText = "Restore aborted (Junction re-created)";
                            return (false, $"Safety Abort: '{targetDataPath}' changed during restore ({postSwapDiff}). The junction was re-created and no data was deleted; close the app and retry.");
                        }
                    }
                    catch { }

                    item.StatusText = "Restore Error — manual check needed";
                    return (false, $"Safety Abort: '{targetDataPath}' changed during restore ({postSwapDiff}). Nothing was deleted: the restored copy is at '{(Directory.Exists(junctionPath) ? junctionPath : stagingPath)}' and the newer offloaded data is still at '{targetDataPath}'. Compare them before removing either.");
                }

                progress?.Report($"[3/3] Cleaning up offloaded copy at '{targetDataPath}'...");
                DeleteDirectoryForce(targetDataPath);
                _manifest.Remove(junctionPath);

                item.IsJunction = false;
                item.JunctionTarget = null;
                var (newClass, _) = _classifier.ClassifyDirectory(junctionPath, false, null);
                item.Classification = newClass;
                item.StatusText = "Restored to OS Drive";

                return (true, $"Successfully restored '{item.Name}' back to '{junctionPath}'.");
            }
            catch (Exception ex)
            {
                item.StatusText = "Restore Error";
                return (false, $"Failed to restore '{item.Name}': {ex.Message}");
            }
        }, ct);
    }

    private string? ValidateRestoreOwnership(string junctionPath, string targetDataPath, string? offloadRoot)
    {
        string? junctionRoot = Path.GetPathRoot(junctionPath);
        string? targetRoot = Path.GetPathRoot(targetDataPath);

        if (string.Equals(junctionRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
        {
            return $"The junction points to '{targetDataPath}' on the same drive. It was not created by SSD Space Guard (likely a built-in Windows link), and restoring it would delete that folder.";
        }

        if (string.Equals(targetDataPath.TrimEnd('\\'), targetRoot?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return $"The junction points to a drive root ('{targetDataPath}'). Refusing to restore.";
        }

        var record = _manifest.Find(junctionPath);
        if (record != null)
        {
            return string.Equals(OffloadManifestService.Normalize(record.DestPath), targetDataPath, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"The junction now points to '{targetDataPath}', but SSD Space Guard offloaded it to '{record.DestPath}'. It was changed outside this app; restore it manually.";
        }

        if (!string.IsNullOrWhiteSpace(offloadRoot))
        {
            string root = OffloadManifestService.Normalize(offloadRoot);
            bool underRoot = targetDataPath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
            bool sameLeaf = string.Equals(Path.GetFileName(targetDataPath), Path.GetFileName(junctionPath), StringComparison.OrdinalIgnoreCase);
            if (underRoot && sameLeaf)
            {
                return null;
            }
        }

        return $"'{junctionPath}' -> '{targetDataPath}' was not created by SSD Space Guard (no offload record, and not under the offload folder '{offloadRoot}'). If you want to undo it, remove the link manually with: rmdir \"{junctionPath}\" and move the data yourself.";
    }

    private static (int FileCount, long ByteCount, string? ErrorMessage) CopyDirectoryVerified(
        string sourceDir,
        string destDir,
        IProgress<string>? progress,
        string label,
        long totalExpectedBytes,
        CancellationToken ct)
    {
        int fileCount = 0;
        long byteCount = 0;
        var sw = Stopwatch.StartNew();

        var dirStack = new Stack<(string Src, string Dst)>();
        dirStack.Push((sourceDir, destDir));

        while (dirStack.Count > 0)
        {
            if (ct.IsCancellationRequested) return (fileCount, byteCount, "Cancelled by user.");
            var (curSrc, curDst) = dirStack.Pop();
            Directory.CreateDirectory(curDst);

            // Preserve directory ACLs & timestamps where supported
            try
            {
                var srcDi = new DirectoryInfo(curSrc);
                var dstDi = new DirectoryInfo(curDst);
                dstDi.SetAccessControl(srcDi.GetAccessControl());
                dstDi.CreationTimeUtc = srcDi.CreationTimeUtc;
                dstDi.LastWriteTimeUtc = srcDi.LastWriteTimeUtc;
            }
            catch { }

            foreach (var filePath in Directory.EnumerateFiles(curSrc))
            {
                if (ct.IsCancellationRequested) return (fileCount, byteCount, "Cancelled by user.");
                string fileName = Path.GetFileName(filePath);
                string dstFile = Path.Combine(curDst, fileName);

                try
                {
                    var fi = new FileInfo(filePath);
                    // Detect Windows AppExecutionAlias / zero-byte reparse files that cannot be copied with File.Copy
                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return (fileCount, byteCount, $"Folder contains special Windows ReparsePoint file '{fileName}' (such as an App Execution Alias) which cannot be relocated.");
                    }

                    File.Copy(filePath, dstFile, overwrite: true);
                    var dstFi = new FileInfo(dstFile);
                    if (dstFi.Length != fi.Length)
                    {
                        return (fileCount, byteCount, $"Size mismatch after copying '{fileName}' (expected {fi.Length} bytes, got {dstFi.Length} bytes).");
                    }

                    try
                    {
                        dstFi.CreationTimeUtc = fi.CreationTimeUtc;
                        dstFi.LastWriteTimeUtc = fi.LastWriteTimeUtc;
                    }
                    catch { }

                    fileCount++;
                    byteCount += fi.Length;
                }
                catch (Exception copyEx)
                {
                    return (fileCount, byteCount, $"File '{fileName}' could not be copied ({copyEx.Message}). A process may be locking it.");
                }

                if (sw.ElapsedMilliseconds >= 400)
                {
                    double pct = totalExpectedBytes > 0 ? Math.Min(99.9, (byteCount * 100.0) / totalExpectedBytes) : 0;
                    progress?.Report($"Copying '{label}': {pct:N1}% ({byteCount / 1048576.0:N1} MB / {totalExpectedBytes / 1048576.0:N1} MB, {fileCount:N0} files)...");
                    sw.Restart();
                }
            }

            foreach (var subDir in Directory.EnumerateDirectories(curSrc))
            {
                var subInfo = new DirectoryInfo(subDir);
                if ((subInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    string subDst = Path.Combine(curDst, subInfo.Name);
                    string? rawTarget = subInfo.LinkTarget;
                    if (string.IsNullOrWhiteSpace(rawTarget))
                    {
                        return (fileCount, byteCount, $"Nested reparse point '{subInfo.FullName}' has an unresolvable target.");
                    }

                    string resolvedTarget = Path.IsPathFullyQualified(rawTarget)
                        ? rawTarget
                        : Path.GetFullPath(rawTarget, subInfo.Parent?.FullName ?? curSrc);

                    if (!Directory.Exists(subDst))
                    {
                        if (!CreateNtfsJunction(subDst, resolvedTarget, out string err))
                        {
                            return (fileCount, byteCount, $"Failed to recreate nested junction '{subInfo.Name}': {err}");
                        }
                    }
                    continue;
                }

                dirStack.Push((subDir, Path.Combine(curDst, subInfo.Name)));
            }
        }

        return (fileCount, byteCount, null);
    }

    /// <summary>
    /// Verifies that every file in sourceRoot exists in destRoot with identical byte length
    /// and LastWriteTimeUtc (within 2 seconds tolerance), and that total file counts match.
    /// </summary>
    private static string? VerifyDirectoryTreesMatch(string sourceRoot, string destRoot)
    {
        var stack = new Stack<(string Src, string Dst)>();
        stack.Push((sourceRoot, destRoot));

        while (stack.Count > 0)
        {
            var (sDir, dDir) = stack.Pop();
            if (!Directory.Exists(dDir))
            {
                return $"Missing destination subdirectory '{dDir}'.";
            }

            var srcFiles = Directory.GetFiles(sDir);
            var dstFiles = Directory.GetFiles(dDir);
            if (srcFiles.Length != dstFiles.Length)
            {
                return $"File count mismatch in '{Path.GetFileName(sDir)}' (source: {srcFiles.Length}, destination: {dstFiles.Length}).";
            }

            foreach (var sFile in srcFiles)
            {
                string fileName = Path.GetFileName(sFile);
                string dFile = Path.Combine(dDir, fileName);
                if (!File.Exists(dFile))
                {
                    return $"File '{fileName}' was created after copy started.";
                }

                var sFi = new FileInfo(sFile);
                var dFi = new FileInfo(dFile);
                if (sFi.Length != dFi.Length)
                {
                    return $"File '{fileName}' size changed during copy ({sFi.Length} vs {dFi.Length} bytes).";
                }

                if (Math.Abs((sFi.LastWriteTimeUtc - dFi.LastWriteTimeUtc).TotalSeconds) > 2.0)
                {
                    return $"File '{fileName}' was modified during copy.";
                }
            }

            foreach (var sSub in Directory.GetDirectories(sDir))
            {
                var subInfo = new DirectoryInfo(sSub);
                string dSub = Path.Combine(dDir, subInfo.Name);
                if ((subInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (!Directory.Exists(dSub))
                        return $"Missing nested junction '{subInfo.Name}'.";
                    continue;
                }
                stack.Push((sSub, dSub));
            }
        }

        return null;
    }

    private static bool CreateNtfsJunction(string linkPath, string targetPath, out string output)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            output = "Failed to start cmd.exe for mklink /J.";
            return false;
        }

        string stdOut = proc.StandardOutput.ReadToEnd();
        string stdErr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        output = (stdOut + " " + stdErr).Trim();
        return proc.ExitCode == 0;
    }

    private static void DeleteDirectoryForce(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return;

        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(dirPath));

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            try
            {
                foreach (var f in cur.EnumerateFiles())
                {
                    try
                    {
                        f.Attributes = FileAttributes.Normal;
                        f.Delete();
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (var sub in cur.EnumerateDirectories())
                {
                    try
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            sub.Delete(false);
                        }
                        else
                        {
                            stack.Push(sub);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        try
        {
            Directory.Delete(dirPath, recursive: true);
        }
        catch { }
    }
}
