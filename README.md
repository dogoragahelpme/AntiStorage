# AntiStorage

AntiStorage is a Windows maintenance app. It cleans disposable local files after you review them, finds duplicate files, and shows where your storage grew between scans.

## Start here

1. Open **AntiStorage.exe** and select **Scan for junk** from **Clean up**.
2. Review the categories and uncheck anything you want to keep. Select **View files** to choose individual files. **Choose what to scan** lets you change the categories for a custom scan.
3. Choose **Clean selected**. The confirmation tells you which files will be deleted and which go to Recovery.
4. For large files and duplicates, inspect the paths first. **Recovery** restores files to existing original folders or permanently deletes them to free up space.

The app skips linked, offline, changed and inaccessible files. Cleanup totals are logical file sizes. They can differ from changes in free disk space because of compression, sparse files and files in use. Recovery keeps moved files on the Windows profile drive, so moving a file there does not itself free disk space.

## Windows feature coverage

| Feature | What AntiStorage does |
| --- | --- |
| Clean up | Scans recommended junk and caches. Review by category or open individual file lists before cleaning. |
| Custom clean | Select rules for temporary files, app and AI caches, shader and thumbnail caches, recent shortcuts, Jump Lists, crash reports, Store temp folders, and older downloads. Includes Recycle Bin management and local Ollama model review. |
| Browser privacy | Cleans Chrome, Edge, Brave and Firefox caches. Browser controls manage history, cookies, session exceptions, saved data and extensions. |
| Registry review | Finds missing executables in simple current user App Paths entries. Backs up selected values and can restore them. |
| Uninstaller | Lists desktop apps. Launches Windows Installer for supported MSI packages and opens Windows Installed Apps for other uninstallers. |
| Duplicate finder | Groups files by size and SHA-256. Keeps a verified copy, supports selecting extras, and moves selected copies to Recovery on the profile drive. |
| Disk analyzer | Scans fixed drives and folders, with minimum size and age filters, a sorted file list and CSV export. |
| Storage activity | Saves the two most recent snapshots for each selected folder and exclusion set. Compare changes and reveal files in Explorer. |
| Cloud storage | Reviews eligible local files in synced folders. Links to OneDrive, Google Drive, Dropbox and Gmail. Online inventory and deletion are handled on those services. |
| Performance | Shows process memory usage in the Windows session. Requests that a selected visible app close and links to startup controls. |
| Startup manager | Lists Run entries, startup shortcuts, services, tasks and context menu entries. Backs up and restores supported per user Run entries. Windows tools manage the other entries. |
| Software updates | Checks the WinGet catalog. Supports installing a selected version, skipping a version and ignoring a package. Requires Microsoft App Installer. |
| Driver updates | Lists installed drivers and checks Windows Update for driver offers. Installs and rolls back through Windows Update and Device Manager. |
| System tools | Hardware and drive inventory, CSV export, and shortcuts to Storage Sense, Optimize Drives, System Restore and Disk Cleanup. |
| Settings and help | Folder exclusions, scan on open, recurring scans while Clean up is open, local activity history, documentation and release links. |

This release covers a subset of CCleaner's feature set. Connected cloud scans and cleanup, temporary app removal and leftover cleanup, whole-app sleep, fully integrated driver install and rollback, advanced registry categories, background cleaning, secure wiping, and file undelete beyond AntiStorage Recovery are outside this build. The separate mobile, business, VPN, and browser products are outside the Windows desktop scope.

Snapshots, backups, settings and Recovery files stay under %LOCALAPPDATA%\AntiStorage. WinGet, Windows Update and browser or cloud tools use their own connections. Scans use the current Windows account's access.

## Build from source

Use Windows with the [.NET Framework 4.8.1 Developer Pack](https://learn.microsoft.com/dotnet/framework/install/guide-for-developers) and MSBuild. AntiStorage uses WPF and has no third-party packages.

    msbuild .\AntiStorage.csproj /p:Configuration=Release
    .\bin\Release\AntiStorage.exe

The framework MSBuild also works when a separate MSBuild installation is unavailable.

    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" .\AntiStorage.csproj /p:Configuration=Release
