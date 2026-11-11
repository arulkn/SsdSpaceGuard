# 🛡️ SSD Space Guard

### **Stop Letting `%LOCALAPPDATA%` Quietly Eat Your Windows Boot SSD.**
*A native .NET 9 WPF utility to surgical-clean gigabytes of hidden cache bloat and transparently offload heavy `AppData`, AI models, SDKs, and WSL2 virtual disks to a secondary NTFS drive—without breaking Windows.*

[![.NET 9](https://img.shields.io/badge/.NET-9.0%20Windows-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/9.0)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?logo=windows)](#)
[![UI](https://img.shields.io/badge/UI-WPF%20Dark%20Fluent-38BDF8)](#)

---

## 💥 The Problem: Why Is Your `C:\` Drive Full When You Install Everything on `D:\`?

Even if you religiously install games and applications to `D:\`, modern desktop apps, browsers, AI tools, and developer toolchains hardcode their storage inside `C:\Users\<You>\AppData\Local`, `AppData\Roaming`, and `%USERPROFILE%`:

- **AI Desktop Apps & Local LLMs**: Claude Desktop VM bundles (`Claude-Data`), Ollama binaries & model weights (`Programs\Ollama`, `.ollama`), Chrome's hidden on-device Gemini Nano weights (`OptGuideOnDeviceModel` — **4+ GB alone**), `.cache` (HuggingFace/uv).
- **Package Manager & Build Hoarders**: `npm-cache` (often **10+ GB**), `pip\cache`, `NuGet\v3-cache`, `pnpm\store`, `Yarn\Cache`, `.gradle\caches`, `ms-playwright` headless browsers.
- **Browser & Electron Bloat**: Multi-profile `Cache_Data`, `Code Cache`, `GPUCache`, `Service Worker\CacheStorage`, `CachedExtensionVSIXs` across Chrome, Edge, Brave, VS Code, Cursor, Slack, Discord, and Teams.
- **WSL2 Virtual Disks**: Linux distributions hiding multi-gigabyte `ext4.vhdx` virtual hard disks inside `AppData\Local\Packages\CanonicalGroupLimited...\LocalState`.

On a 240 GB or 512 GB boot SSD, `AppData` alone easily balloons past **70+ GB**, starving Windows of swap and update space.

---

## ⚡ How SSD Space Guard Fixes It

**SSD Space Guard** tackles the problem in three distinct, non-destructive ways:

### 1. 🧹 Surgical Smart Cache Cleaner (Tab 2)
Auto-discovers transient caches across package managers, web browsers (all profiles), Electron/IDE apps, crash dumps, RDP bitmap caches, and `%TEMP%`.
- **Browser Safe-Mode**: Purges only transient compute/network caches (`Cache_Data`, `Code Cache`, `GPUCache`, `ShaderCache`, `Service Worker\CacheStorage`). **Your logins, cookies, history, bookmarks, and extensions are never touched.**
- **Active-Installer Guard**: Skips any locked in-use files and ignores `%TEMP%` files modified within the last **60 minutes** so active background installers are never disrupted.

### 2. 🔗 Transactional NTFS Junction Offloader & 1-Click Restore (Tab 1)
Moves heavy folders physically to a secondary Fixed NTFS drive (e.g., `D:\SSD_Offload\...`) and places a **Windows NTFS Directory Junction (`mklink /J`)** at the original `C:\` path.
- **100% Transparent to Apps**: Windows resolves NTFS junctions at the file-system kernel level. Applications continue reading and writing `C:\Users\<You>\AppData\Local\<App>` without knowing the bytes now live on `D:\`.
- **1-Click Staged Restore**: Any junction created by SSD Space Guard is tracked in `%LOCALAPPDATA%\SsdSpaceGuard\offload_manifest.json` and can be restored back to `C:\` at any time.

### 3. 🐧 WSL2 Virtual Disk (`.vhdx`) Relocator (Tab 3)
Reads `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss` to find registered WSL2 Linux distributions and safely moves their `ext4.vhdx` virtual disks to your secondary drive using Microsoft's official `wsl --shutdown` + `wsl --manage <Distro> --move` pipeline.

---

## ⚠️ Understand the Risks Involved & How SSD Space Guard Protects You

Manipulating `AppData` and `UserProfile` folders on Windows is **inherently high-risk if done carelessly** (e.g., with manual drag-and-drop or naive `mklink` scripts). Below are the **real-world hazards that can corrupt a Windows installation or cause data loss**, how **SSD Space Guard** blocks them in code, and what you must still keep in mind as a user.

### 🔴 1. What Can Go Wrong When Offloading `AppData` (And How We Block It)

| Risk / Failure Mode | What Happens If You Do It Naively | How SSD Space Guard Prevents It |
| :--- | :--- | :--- |
| **Offloading Windows Shell, Registry Hives, or DPAPI Keys**<br>(`AppData\Local\Microsoft\Windows`, `AppData\Roaming\Microsoft`, `Credentials`, `Protect`, `Crypto`, `TokenBroker`) | Contains `UsrClass.dat` (per-user Explorer/Start Menu registry hive), DPAPI encryption master keys, certificates, and login tokens. Moving or locking them breaks Windows login, Start Menu, and saved passwords. | **Hard-Blocked (`SystemProtected`)** in [`ClassificationService.cs`](SsdSpaceGuard/Services/ClassificationService.cs). The UI disables offloading and the backend refuses execution even if invoked directly. |
| **Junctioning Microsoft Store / UWP / MSIX Packages**<br>(`AppData\Local\Packages\*`) | Windows `AppContainer` kernel sandboxes enforce strict per-package SID ACLs (`S-1-15-2-*`) on `AppData\Local\Packages`. Standard junctions break sandbox token checks, preventing Store apps (Windows Terminal, Snipping Tool, Calculator, etc.) from launching. | **Hard-Blocked (`SystemProtected`)**: All `AppData\Local\Packages\*` folders are blocked from NTFS junctioning. To move a Store app, use *Windows Settings → Installed Apps → Move*. For WSL2 `.vhdx` disks inside `Packages`, use Tab 3 (`wsl --manage`). |
| **"Restoring" Built-In Windows Compatibility Junctions**<br>(`My Documents` → `Documents`, `Application Data` → `AppData\Roaming`, `Local\Application Data` → `AppData\Local`) | Windows ships hidden legacy junctions inside `%USERPROFILE%`. Because a "Restore" operation copies a junction's target and then deletes the target folder, running Restore on `My Documents` in a naive tool would **delete your real `Documents` folder**. | **Triple-Locked**:<br>1. All OS/Shell protection rules run *before* junction classification.<br>2. Same-drive junctions are classified as `SystemProtected` (`CanRestore = false`).<br>3. [`OffloadManifestService.cs`](SsdSpaceGuard/Services/OffloadManifestService.cs) & `ValidateRestoreOwnership()` refuse to restore any junction not demonstrably created by SSD Space Guard. |
| **Turning `%TEMP%` (`AppData\Local\Temp`) into a Junction** | Windows Installer (`msiexec`) and Windows Update block directory junctions on `Temp` as a privilege-escalation mitigation, causing `.msi` setups to fail with Error 1603. | `AppData\Local\Temp` is **blocked from junctioning** (`SystemProtected`) and is only cleaned in Tab 2 (with a 60-minute recency guard). |
| **Copying Windows App Execution Aliases**<br>(`AppData\Local\Microsoft\WindowsApps`) | Contains zero-byte `IO_REPARSE_TAG_APPEXECLINK` kernel reparse files (`python.exe`, `winget.exe`, `wt.exe`) that standard `File.Copy` cannot duplicate. | `Microsoft\WindowsApps` is `SystemProtected`, and the copy engine aborts immediately if it ever encounters a file with `FileAttributes.ReparsePoint`. |
| **Moving a Folder While an App Is Actively Writing to It** | Open SQLite databases, LevelDB stores, or log files can be corrupted or lose in-flight writes during a copy. | 1. Queries the **Win32 Restart Manager API (`rstrtmgr.dll` `RmGetList`)** and active processes to block offloading if any process holds open file handles.<br>2. Renames the source to `._ssdguard_backup` first, then runs `VerifyDirectoryTreesMatch()` (checking file count, byte size, and `LastWriteTimeUtc`) before creating the junction or deleting the backup. |
| **Power Loss or User Cancellation Mid-Move / Mid-Restore** | Naive scripts delete files as they move, leaving half the folder on `C:\` and half on `D:\`. | **Atomic Staging & Rollback**: Copies 100% of files first, verifies parity, swaps via atomic rename (`._ssdguard_backup` / `._restore_staging`), verifies the junction, and only then removes the backup. Cancelling cleans up the partial copy and leaves the original untouched. |
| **Offloading to a Removable USB Drive or `exFAT`/`FAT32` Drive** | Unplugging a USB drive or lacking NTFS ACLs causes immediate application crashes. | Hard-requires an internal **Fixed (`DriveType.Fixed`) NTFS** volume with at least **512 MB** (or **1 GB** for WSL2) of extra free space buffer. |

---

### 🟡 2. Residual Risks & User Responsibilities (Read Before Using)

Even with transactional rollbacks and OS blocklists, **NTFS Junction offloading redirects live application storage across physical disks**. Keep these rules in mind:

1. **Do Not Remove, Format, or Change the Drive Letter of Your Target Drive (`D:\`)**:
   - Once a folder is offloaded via NTFS Junction (`C:\Users\<You>\AppData\Local\<App>` → `D:\SSD_Offload\...`), that application depends on `D:\` being online and keeping the `D:\` drive letter.
   - If you plan to replace or format `D:\`, open **SSD Space Guard** and click **Restore** on your offloaded folders first so they are moved back to `C:\`.
2. **Close Target Applications Before Offloading**:
   - While SSD Space Guard uses the Windows Restart Manager (`RmGetList`) to detect open file handles, background helper daemons that briefly open and close files between checks can still interfere. Close the application (and its system tray icon) before offloading its folder.
3. **Antivirus & Self-Protecting Apps**:
   - Security software, anti-cheat engines, and kernel-driver utilities often verify that their `AppData` directories are not reparse points. Do not offload antivirus or anti-cheat folders.
4. **Editing `rules.json` (Tab 4)**:
   - Removing entries from `ProtectedFolderPatterns` in `rules.json` weakens OS directory protection. Only add patterns unless you know the exact impact of removing one.

---

## 🚀 Getting Started

### Prerequisites
- **OS**: Windows 10 or Windows 11
- **Runtime / SDK**: [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### Build & Run
```powershell
# Clone the repository
git clone https://github.com/arulkn/SsdSpaceGuard.git
cd SsdSpaceGuard

# Build Release binary
dotnet build SsdSpaceGuard.sln -c Release

# Launch SSD Space Guard
Start-Process .\SsdSpaceGuard\bin\Release\net9.0-windows\SsdSpaceGuard.exe
```

### Run Automated Safety Self-Test
```powershell
Start-Process -FilePath .\SsdSpaceGuard\bin\Release\net9.0-windows\SsdSpaceGuard.exe -ArgumentList "--self-test" -Wait
Get-Content .\SsdSpaceGuard\bin\Release\net9.0-windows\selftest_result.txt
```

---

## 📖 Deep-Dive Architecture Documentation

See **[ARCHITECTURE_AND_GUIDE.md](ARCHITECTURE_AND_GUIDE.md)** for Mermaid system diagrams, a file-by-file breakdown of every service and model, and the complete security/safety review log.
