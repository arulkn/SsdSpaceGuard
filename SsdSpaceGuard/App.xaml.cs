using System.IO;
using System.Windows;
using SsdSpaceGuard.Services;

namespace SsdSpaceGuard;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            System.Windows.MessageBox.Show(
                $"An unexpected UI error occurred:\n{args.Exception.Message}",
                "SSD Space Guard",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
        };

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            var classifier = new ClassificationService();
            var scanner = new DiskScannerService(classifier);
            var cacheService = new CachePurgeService(classifier);

            var drives = scanner.GetReadyFixedDrives();
            var caches = await cacheService.DiscoverCachesAsync();

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var testPaths = new[]
            {
                @"C:\Windows",
                @"C:\Program Files",
                Path.Combine(userProfile, "Desktop"),
                Path.Combine(userProfile, "OneDrive"),
                Path.Combine(localAppData, "Temp"),
                Path.Combine(localAppData, @"Microsoft\Windows"),
                Path.Combine(localAppData, @"Microsoft\WindowsApps"),
                Path.Combine(roamingAppData, "Microsoft"),
                Path.Combine(roamingAppData, @"Microsoft\Crypto"),
                Path.Combine(localAppData, @"Packages\Clipchamp.Clipchamp_yxz26nhyzhsrt"),
                Path.Combine(localAppData, "Claude-Data"),
                Path.Combine(localAppData, "npm-cache")
            };

            var checkLines = testPaths.Select(p =>
            {
                var (cls, reason) = classifier.ClassifyDirectory(p, false, null);
                return $"  {p} -> {cls} ({reason})";
            });

            var junctionCases = new (string Path, string Target)[]
            {
                (Path.Combine(userProfile, "My Documents"), Path.Combine(userProfile, "Documents")),
                (Path.Combine(userProfile, "Application Data"), roamingAppData),
                (Path.Combine(localAppData, "Application Data"), localAppData)
            };
            bool allJunctionsSafe = true;
            var junctionLines = junctionCases.Select(c =>
            {
                var (cls, reason) = classifier.ClassifyDirectory(c.Path, true, c.Target);
                bool pass = cls == SsdSpaceGuard.Models.FolderClassification.SystemProtected;
                allJunctionsSafe &= pass;
                return $"  [{(pass ? "PASS" : "FAIL")}] {c.Path} -> {c.Target}: {cls} ({reason})";
            }).ToList();

            bool roamMsProtected = classifier.ClassifyDirectory(Path.Combine(roamingAppData, "Microsoft"), false, null).Classification
                                   == SsdSpaceGuard.Models.FolderClassification.SystemProtected;
            bool badJsonRejected = !ClassificationService.TryValidateRulesJson("{ bad json", out _, out _) &&
                                   !ClassificationService.TryValidateRulesJson("{\"ProtectedFolderPatterns\":[]}", out _, out _);

            bool allPass = allJunctionsSafe && roamMsProtected && badJsonRejected;

            string report = string.Join(Environment.NewLine,
                "=== SSD Space Guard Hardened Safety Self-Test ===",
                $"Active Rules Path: {classifier.ActiveRulesPath} (Healthy: {classifier.RulesHealthy})",
                $"Detected Drives: {string.Join(" | ", drives.Select(d => d.DisplayTitle))}",
                $"Discovered Cache Groups: {caches.Count} ({caches.Sum(c => c.SizeBytes) / 1048576.0:N1} MB)",
                $"Roaming\\Microsoft Protected: {roamingAppData}\\Microsoft -> {(roamMsProtected ? "PASS" : "FAIL")}",
                $"Malformed/Empty Rules JSON Rejected: {(badJsonRejected ? "PASS" : "FAIL")}",
                "Safety Classification Audit:",
                string.Join(Environment.NewLine, checkLines),
                "Windows Compatibility Junction Audit (must be SystemProtected):",
                string.Join(Environment.NewLine, junctionLines)
            );

            string outFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest_result.txt");
            File.WriteAllText(outFile, report);
            Shutdown(allPass ? 0 : 1);
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
