using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SsdSpaceGuard.Models;
using SsdSpaceGuard.Services;

namespace SsdSpaceGuard;

public partial class MainWindow : Window
{
    private readonly ClassificationService _classifier;
    private readonly DiskScannerService _scanner;
    private readonly CachePurgeService _cacheService;
    private readonly JunctionMigrationService _junctionService;
    private readonly WslRelocatorService _wslService;

    private readonly ObservableCollection<DriveStats> _drives = new();
    private readonly ObservableCollection<ScannedFolderItem> _folders = new();
    private readonly ObservableCollection<CacheTargetItem> _caches = new();
    private readonly ObservableCollection<VirtualDiskItem> _vhdxItems = new();
    private readonly List<string> _customScanRoots = new();

    private ICollectionView? _foldersView;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();

        _classifier = new ClassificationService();
        _scanner = new DiskScannerService(_classifier);
        _cacheService = new CachePurgeService(_classifier);
        _junctionService = new JunctionMigrationService(_classifier);
        _wslService = new WslRelocatorService(_scanner);

        DrivesItemsControl.ItemsSource = _drives;
        CachesGrid.ItemsSource = _caches;
        VhdxGrid.ItemsSource = _vhdxItems;

        _foldersView = CollectionViewSource.GetDefaultView(_folders);
        _foldersView.Filter = FilterFolderPredicate;
        FoldersGrid.ItemsSource = _foldersView;

        LoadRulesIntoEditor();
        RefreshDriveCards(selectDefaultTarget: true);

        Loaded += async (_, _) => await RunFullSystemScanAsync();
    }

    private void RefreshDriveCards(bool selectDefaultTarget = false)
    {
        var readyDrives = _scanner.GetReadyFixedDrives();
        _drives.Clear();
        foreach (var d in readyDrives)
        {
            _drives.Add(d);
        }

        if (selectDefaultTarget)
        {
            var targetCandidates = readyDrives
                .Where(d => !d.IsOsDrive && string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.FreeBytes)
                .ToList();

            if (targetCandidates.Count == 0)
            {
                targetCandidates = readyDrives.OrderByDescending(d => d.FreeBytes).ToList();
            }

            TargetDriveCombo.ItemsSource = targetCandidates;
            if (targetCandidates.Count > 0)
            {
                TargetDriveCombo.SelectedIndex = 0;
            }
        }
    }

    private void TargetDriveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetDriveCombo.SelectedItem is DriveStats selectedDrive)
        {
            OffloadRootTextBox.Text = Path.Combine(selectedDrive.Name, "SSD_Offload");
        }
    }

    private async void ScanAllButton_Click(object sender, RoutedEventArgs e)
    {
        await RunFullSystemScanAsync();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        AppendLog("Cancellation requested by user...");
    }

    private async Task RunFullSystemScanAsync()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        SetBusyState(true, "Scanning drives, AppData, user profile folders, caches, and virtual disks...");

        try
        {
            RefreshDriveCards(selectDefaultTarget: false);

            long minMB = 50;
            if (long.TryParse(MinSizeTextBox.Text.Trim(), out long parsedMB) && parsedMB >= 1)
            {
                minMB = parsedMB;
            }
            long minBytes = minMB * 1024L * 1024L;

            var progress = new Progress<string>(msg =>
            {
                StatusBannerText.Text = msg;
            });

            // 1. Scan directories
            var scannedFolders = await _scanner.ScanStandardAndCustomRootsAsync(
                minBytes, _customScanRoots, progress, _cts.Token);

            _folders.Clear();
            foreach (var f in scannedFolders)
            {
                f.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ScannedFolderItem.IsSelected))
                        UpdateFolderSummary();
                };
                _folders.Add(f);
            }
            UpdateFolderSummary();

            // 2. Discover safe caches
            var discoveredCaches = await _cacheService.DiscoverCachesAsync(progress, _cts.Token);
            _caches.Clear();
            foreach (var c in discoveredCaches)
            {
                c.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(CacheTargetItem.IsSelected) ||
                        args.PropertyName == nameof(CacheTargetItem.SizeBytes))
                    {
                        UpdateCacheSummary();
                    }
                };
                _caches.Add(c);
            }
            UpdateCacheSummary();

            // 3. Discover WSL2 & .vhdx virtual disks
            var vhds = await _wslService.DiscoverVirtualDisksAsync(progress, _cts.Token);
            _vhdxItems.Clear();
            foreach (var v in vhds)
            {
                _vhdxItems.Add(v);
            }

            long totalCacheBytes = _caches.Sum(c => c.SizeBytes);
            long totalOffloadableBytes = _folders.Where(f => f.CanOffload).Sum(f => f.SizeBytes);
            AppendLog($"Scan complete: {_folders.Count} folders (>= {minMB} MB), {_caches.Count} cache groups ({totalCacheBytes / 1073741824.0:N2} GB reclaimable), {_vhdxItems.Count} virtual disks.");
            StatusBannerText.Text = $"Scan complete — Reclaimable Caches: {totalCacheBytes / 1073741824.0:N2} GB | Offloadable AppData/Profile Folders: {totalOffloadableBytes / 1073741824.0:N2} GB.";
        }
        catch (OperationCanceledException)
        {
            StatusBannerText.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"Scan error: {ex.Message}");
            StatusBannerText.Text = "Scan encountered an error.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusyState(false);
        }
    }

    private bool FilterFolderPredicate(object obj)
    {
        if (obj is not ScannedFolderItem item) return false;

        if (ScopeFilterCombo?.SelectedItem is ComboBoxItem selectedScope)
        {
            string scopeText = selectedScope.Content?.ToString() ?? "All Scanned Locations";
            if (scopeText != "All Scanned Locations" &&
                !item.ScopeCategory.Equals(scopeText, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string query = SearchFilterBox?.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(query))
        {
            return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   item.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   item.ClassificationBadge.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private void ScopeFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _foldersView?.Refresh();
        UpdateFolderSummary();
    }

    private void SearchFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _foldersView?.Refresh();
        UpdateFolderSummary();
    }

    private void AddCustomFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a folder on your OS drive to analyze or offload via NTFS Junction",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            if (!_customScanRoots.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase))
            {
                _customScanRoots.Add(dlg.SelectedPath);
                AppendLog($"Added custom folder to scan list: {dlg.SelectedPath}");
                _ = RunFullSystemScanAsync();
            }
        }
    }

    private void SelectRecommendedFolders_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _folders)
        {
            f.IsSelected = f.CanOffload &&
                           (f.Classification == FolderClassification.RecommendedOffload ||
                            f.Classification == FolderClassification.SafeCachePurge);
        }
        UpdateFolderSummary();
    }

    private void ClearFolderSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _folders) f.IsSelected = false;
        UpdateFolderSummary();
    }

    private void UpdateFolderSummary()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateFolderSummary);
            return;
        }

        if (FolderSummaryText == null) return;
        var selected = _folders.Where(f => f.IsSelected && f.CanOffload).ToList();
        long selBytes = selected.Sum(f => f.SizeBytes);
        FolderSummaryText.Text = selected.Count > 0
            ? $"Selected to Offload: {selected.Count} folder(s) ({selBytes / 1073741824.0:N2} GB)"
            : $"Showing {_folders.Count} folders";
    }

    private void UpdateCacheSummary()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateCacheSummary);
            return;
        }

        if (CacheSummaryText == null) return;
        var selected = _caches.Where(c => c.IsSelected).ToList();
        long selBytes = selected.Sum(c => c.SizeBytes);
        CacheSummaryText.Text = $"Selected Reclaimable: {selBytes / 1073741824.0:N2} GB ({selBytes / 1048576.0:N1} MB)";
    }

    private void SelectAllCaches_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _caches) c.IsSelected = true;
        UpdateCacheSummary();
    }

    private void DeselectAllCaches_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _caches) c.IsSelected = false;
        UpdateCacheSummary();
    }

    private async void PurgeCachesButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _caches.Where(c => c.IsSelected && c.SizeBytes > 0).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show("Please check at least one cache item to purge.", "No Caches Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long estBytes = selected.Sum(c => c.SizeBytes);
        var confirm = System.Windows.MessageBox.Show(
            $"Purge {selected.Count} selected cache group(s) (~{estBytes / 1073741824.0:N2} GB)?\n\n" +
            "All user data, browser passwords, cookies, and settings are preserved. In-use files locked by open apps will be safely skipped.",
            "Confirm Safe Cache Purge",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        SetBusyState(true, "Purging selected caches...");

        try
        {
            var progress = new Progress<string>(msg =>
            {
                StatusBannerText.Text = msg;
                AppendLog(msg);
            });

            var (freedBytes, deletedFiles, skippedFiles) = await _cacheService.PurgeSelectedCachesAsync(selected, progress, _cts.Token);
            RefreshDriveCards(selectDefaultTarget: false);
            UpdateCacheSummary();

            string summary = $"Cache purge completed! Freed {freedBytes / 1073741824.0:N2} GB ({freedBytes / 1048576.0:N1} MB) across {deletedFiles:N0} files ({skippedFiles:N0} locked files skipped).";
            AppendLog(summary);
            StatusBannerText.Text = summary;
            System.Windows.MessageBox.Show(summary, "Cache Purge Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cache purge cancelled by user.");
            StatusBannerText.Text = "Cache purge cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"Cache purge error: {ex.Message}");
            StatusBannerText.Text = "Cache purge encountered an error.";
            System.Windows.MessageBox.Show($"An error occurred during cache purge:\n{ex.Message}", "Cache Purge Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusyState(false);
        }
    }

    private async void SingleOffloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScannedFolderItem item })
        {
            await OffloadFoldersInternalAsync([item]);
        }
    }

    private async void BatchOffloadButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _folders.Where(f => f.IsSelected && f.CanOffload).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show("Check one or more offloadable folders first.", "No Folders Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await OffloadFoldersInternalAsync(selected);
    }

    private async Task OffloadFoldersInternalAsync(List<ScannedFolderItem> itemsToOffload)
    {
        string targetRoot = OffloadRootTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            System.Windows.MessageBox.Show("Please specify a valid Target Offload Folder (e.g. D:\\SSD_Offload).",
                "Target Folder Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long totalBytes = itemsToOffload.Sum(i => i.SizeBytes);
        string folderListPreview = string.Join("\n• ", itemsToOffload.Take(8).Select(i => $"{i.Name} ({i.SizeFormatted})"));
        if (itemsToOffload.Count > 8) folderListPreview += $"\n...and {itemsToOffload.Count - 8} more";

        var confirm = System.Windows.MessageBox.Show(
            $"Offload {itemsToOffload.Count} folder(s) ({totalBytes / 1073741824.0:N2} GB) to '{targetRoot}' via NTFS Directory Junction?\n\n" +
            $"• {folderListPreview}\n\n" +
            "How it works:\n" +
            "1. Verifies no running process or open file handle is locking the folder.\n" +
            "2. Copies all files to the target drive preserving timestamps & ACLs.\n" +
            "3. Verifies 100% file parity before creating the NTFS Junction (mklink /J).\n" +
            "4. Records ownership in the manifest and removes the backup only after verification.",
            "Confirm NTFS Junction Offload",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        SetBusyState(true, "Offloading folders via NTFS Junction...");

        int succeeded = 0;
        int failed = 0;

        try
        {
            var progress = new Progress<string>(msg =>
            {
                StatusBannerText.Text = msg;
            });

            foreach (var item in itemsToOffload)
            {
                if (_cts.IsCancellationRequested) break;
                AppendLog($"Starting offload: '{item.FullPath}' -> '{targetRoot}'...");
                var (ok, message) = await _junctionService.OffloadFolderWithJunctionAsync(item, targetRoot, progress, _cts.Token);
                AppendLog(message);
                if (ok)
                {
                    succeeded++;
                    item.IsSelected = false;
                }
                else
                {
                    failed++;
                }
                RefreshDriveCards(selectDefaultTarget: false);
            }

            UpdateFolderSummary();
            string doneMsg = $"Offload finished: {succeeded} succeeded, {failed} skipped/failed.";
            StatusBannerText.Text = doneMsg;
            System.Windows.MessageBox.Show(doneMsg + "\nSee the Activity Log at the bottom for full details.",
                "NTFS Junction Offload Summary", MessageBoxButton.OK,
                failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppendLog($"Offload error: {ex.Message}");
            StatusBannerText.Text = "Offload encountered an error.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusyState(false);
        }
    }

    private async void SingleRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ScannedFolderItem item } || !item.CanRestore)
            return;

        var confirm = System.Windows.MessageBox.Show(
            $"Restore '{item.Name}' ({item.SizeFormatted}) from '{item.JunctionTarget}' back to its physical location on your OS drive ({item.FullPath})?",
            "Confirm Restore to OS Drive",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        SetBusyState(true, $"Restoring '{item.Name}' back to OS drive...");

        try
        {
            var progress = new Progress<string>(msg => StatusBannerText.Text = msg);
            var (ok, message) = await _junctionService.RestoreJunctionToOriginalDriveAsync(item, progress, _cts.Token, offloadRoot: OffloadRootTextBox.Text.Trim());
            AppendLog(message);
            StatusBannerText.Text = message;
            RefreshDriveCards(selectDefaultTarget: false);
            System.Windows.MessageBox.Show(message, "Restore Status", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppendLog($"Restore error: {ex.Message}");
            StatusBannerText.Text = "Restore encountered an error.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusyState(false);
        }
    }

    private void OpenFolderInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScannedFolderItem item } && Directory.Exists(item.FullPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{item.FullPath}\"",
                UseShellExecute = true
            });
        }
    }

    private async void RelocateVhdxButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VirtualDiskItem vhd }) return;

        if (TargetDriveCombo.SelectedItem is not DriveStats targetDrive)
        {
            System.Windows.MessageBox.Show("Select a Target Offload Drive first.", "No Target Drive",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Relocate virtual disk '{vhd.DistroOrOwner}' ({vhd.SizeFormatted}) to '{targetDrive.Name}WSL_Offload'?\n\n" +
            "Note: This will run 'wsl --shutdown' to safely unmount running WSL2 instances before moving the virtual disk.",
            "Confirm WSL2 / Virtual Disk Relocation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        SetBusyState(true, $"Relocating '{vhd.DistroOrOwner}' to {targetDrive.Name}...");

        try
        {
            var progress = new Progress<string>(msg =>
            {
                StatusBannerText.Text = msg;
                AppendLog(msg);
            });

            var (ok, message) = await _wslService.RelocateVirtualDiskAsync(vhd, targetDrive.Name, progress, _cts.Token);
            AppendLog(message);
            StatusBannerText.Text = message;
            RefreshDriveCards(selectDefaultTarget: false);
            System.Windows.MessageBox.Show(message, "Virtual Disk Relocation", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppendLog($"WSL relocation error: {ex.Message}");
            StatusBannerText.Text = "WSL relocation encountered an error.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusyState(false);
        }
    }

    private void LoadRulesIntoEditor()
    {
        try
        {
            string configPath = File.Exists(_classifier.ActiveRulesPath)
                ? _classifier.ActiveRulesPath
                : _classifier.DefaultTemplateRulesPath;
            if (File.Exists(configPath))
            {
                RulesJsonEditor.Text = File.ReadAllText(configPath);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not load rules.json: {ex.Message}");
        }
    }

    private void ReloadRulesButton_Click(object sender, RoutedEventArgs e)
    {
        _classifier.LoadRules();
        if (!_classifier.RulesHealthy && File.Exists(_classifier.DefaultTemplateRulesPath))
        {
            File.Copy(_classifier.DefaultTemplateRulesPath, _classifier.ActiveRulesPath, overwrite: true);
            _classifier.LoadRules();
        }
        LoadRulesIntoEditor();
        AppendLog($"Reloaded rules.json from '{_classifier.ActiveRulesPath}'.");
    }

    private async void SaveRulesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string candidateJson = RulesJsonEditor.Text;
            if (!ClassificationService.TryValidateRulesJson(candidateJson, out _, out string? validationError))
            {
                System.Windows.MessageBox.Show(
                    $"Cannot save rules.json:\n\n{validationError}\n\nPlease fix the JSON syntax before saving.",
                    "Invalid Rules JSON",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string configPath = _classifier.ActiveRulesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            string tmpPath = configPath + ".tmp";
            File.WriteAllText(tmpPath, candidateJson);
            File.Move(tmpPath, configPath, overwrite: true);

            _classifier.LoadRules();
            AppendLog($"Validated and saved rules.json to '{configPath}'.");
            await RunFullSystemScanAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to save rules.json: {ex.Message}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetBusyState(bool isBusy, string? message = null)
    {
        ScanAllButton.IsEnabled = !isBusy;
        BatchOffloadButton.IsEnabled = !isBusy;
        PurgeCachesButton.IsEnabled = !isBusy;
        CancelButton.IsEnabled = isBusy;
        BusyProgressBar.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        BusyProgressBar.IsIndeterminate = isBusy;
        if (!string.IsNullOrEmpty(message))
        {
            StatusBannerText.Text = message;
        }
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            ActivityLogBox.AppendText(line + Environment.NewLine);
            ActivityLogBox.ScrollToEnd();
        });
    }
}
