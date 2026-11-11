using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SsdSpaceGuard.Models;

internal static class UiDispatcherHelper
{
    public static void RaisePropertyChanged(PropertyChangedEventHandler? handler, object sender, string? propertyName)
    {
        if (handler == null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => handler.Invoke(sender, new PropertyChangedEventArgs(propertyName)));
        }
        else
        {
            handler.Invoke(sender, new PropertyChangedEventArgs(propertyName));
        }
    }
}

public enum FolderClassification
{
    GeneralAppData,
    RecommendedOffload,
    SafeCachePurge,
    VirtualDiskContainer,
    SystemProtected,
    AlreadyOffloadedJunction
}

public class DriveStats : INotifyPropertyChanged
{
    public string Name { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public string DriveFormat { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool IsOsDrive { get; set; }

    public double TotalGB => Math.Round(TotalBytes / 1073741824.0, 2);
    public double FreeGB => Math.Round(FreeBytes / 1073741824.0, 2);
    public double UsedGB => Math.Round((TotalBytes - FreeBytes) / 1073741824.0, 2);
    public double UsedPercent => TotalBytes > 0 ? Math.Round(((double)(TotalBytes - FreeBytes) / TotalBytes) * 100.0, 1) : 0;

    public string DisplayTitle => IsOsDrive
        ? $"{Name} (OS Boot SSD) — {FreeGB:N1} GB Free / {TotalGB:N1} GB"
        : $"{Name} {(string.IsNullOrWhiteSpace(VolumeLabel) ? "Data Volume" : VolumeLabel)} — {FreeGB:N1} GB Free / {TotalGB:N1} GB ({DriveFormat})";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyUpdated()
    {
        UiDispatcherHelper.RaisePropertyChanged(PropertyChanged, this, null);
    }
}

public class ScannedFolderItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private long _sizeBytes;
    private bool _isJunction;
    private string? _junctionTarget;
    private FolderClassification _classification;
    private string _statusText = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string ScopeCategory { get; set; } = string.Empty;

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            _sizeBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeFormatted));
            OnPropertyChanged(nameof(SizeMB));
        }
    }

    public double SizeMB => Math.Round(SizeBytes / 1048576.0, 1);

    public string SizeFormatted => SizeBytes >= 1073741824L
        ? $"{SizeBytes / 1073741824.0:N2} GB"
        : $"{SizeBytes / 1048576.0:N1} MB";

    public bool IsJunction
    {
        get => _isJunction;
        set
        {
            _isJunction = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkBadge));
            OnPropertyChanged(nameof(CanOffload));
            OnPropertyChanged(nameof(CanRestore));
        }
    }

    public string? JunctionTarget
    {
        get => _junctionTarget;
        set
        {
            _junctionTarget = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkBadge));
        }
    }

    public FolderClassification Classification
    {
        get => _classification;
        set
        {
            _classification = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ClassificationBadge));
            OnPropertyChanged(nameof(CanOffload));
            OnPropertyChanged(nameof(CanRestore));
        }
    }

    public string RecommendationReason { get; set; } = string.Empty;

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public bool CanOffload => !IsJunction
                              && Classification != FolderClassification.SystemProtected
                              && Classification != FolderClassification.VirtualDiskContainer;

    public bool CanRestore => IsJunction
                              && Classification == FolderClassification.AlreadyOffloadedJunction
                              && !string.IsNullOrWhiteSpace(JunctionTarget);

    public string LinkBadge => IsJunction
        ? $"Linked -> {JunctionTarget}"
        : "Physical (On OS Drive)";

    public string ClassificationBadge => Classification switch
    {
        FolderClassification.AlreadyOffloadedJunction => "Offloaded (NTFS Junction)",
        FolderClassification.RecommendedOffload => "Recommended for Junction Offload",
        FolderClassification.SafeCachePurge => "Cache / Temp (Purge or Offload)",
        FolderClassification.VirtualDiskContainer => "Virtual Disk (.vhdx) — Use WSL/VHD Tab",
        FolderClassification.SystemProtected => "System Protected (Keep on OS Drive)",
        _ => "Standard App Folder (Offloadable)"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? prop = null)
        => UiDispatcherHelper.RaisePropertyChanged(PropertyChanged, this, prop);
}

public class CacheTargetItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private long _sizeBytes;
    private string _status = "Ready";

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> TargetPaths { get; set; } = new();

    public string PrimaryPathDisplay => TargetPaths.Count == 1
        ? TargetPaths[0]
        : $"{TargetPaths.FirstOrDefault()} (+{TargetPaths.Count - 1} sub-paths)";

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            _sizeBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeFormatted));
        }
    }

    public string SizeFormatted => SizeBytes >= 1073741824L
        ? $"{SizeBytes / 1073741824.0:N2} GB"
        : $"{SizeBytes / 1048576.0:N1} MB";

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? prop = null)
        => UiDispatcherHelper.RaisePropertyChanged(PropertyChanged, this, prop);
}

public class VirtualDiskItem : INotifyPropertyChanged
{
    private string _status = "Ready";
    public string Name { get; set; } = string.Empty;
    public string DistroOrOwner { get; set; } = string.Empty;
    public string VhdxPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool IsWslDistro { get; set; }
    public bool IsOnOsDrive { get; set; }

    public string SizeFormatted => SizeBytes >= 1073741824L
        ? $"{SizeBytes / 1073741824.0:N2} GB"
        : $"{SizeBytes / 1048576.0:N1} MB";

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            UiDispatcherHelper.RaisePropertyChanged(PropertyChanged, this, nameof(Status));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class RuleConfig
{
    public List<string> ProtectedFolderPatterns { get; set; } = new();
    public List<KnownCacheSignature> KnownCacheSignatures { get; set; } = new();
    public List<BrowserRootRule> BrowserRoots { get; set; } = new();
    public List<string> ChromiumProfileCacheSubfolders { get; set; } = new();
    public List<string> ElectronCacheSubfolders { get; set; } = new();
    public List<string> GoodOffloadHints { get; set; } = new();
}

public class KnownCacheSignature
{
    public string Category { get; set; } = string.Empty;
    public string NamePattern { get; set; } = string.Empty;
    public string? SubPath { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool RecommendJunction { get; set; }
}

public class BrowserRootRule
{
    public string VendorName { get; set; } = string.Empty;
    public string RelativeUserDataPath { get; set; } = string.Empty;
}
