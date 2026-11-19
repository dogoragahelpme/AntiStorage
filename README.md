# AntiStorage

**[Download AntiStorage.exe for Windows](https://github.com/dogoragahelpme/AntiStorage/releases/download/v0.1.0/AntiStorage.exe)**

AntiStorage is a Windows desktop prototype for finding which files changed a folder's size. It saves the two most recent scans of each folder and exclusion set so you can compare them after restarting the app.

## Try the prototype

1. Open `AntiStorage.exe`.
2. Choose a folder and select **Scan folder**. The first scan shows its largest files and establishes a baseline.
3. Add or resize a test file and scan the same folder again. **Changes** shows each changed path, its previous and current size, and the difference. Select an existing file and choose **Show in Explorer** to inspect it.
4. Open **Temporary files** to review files under `%LOCALAPPDATA%\Temp` that were created and last modified at least seven days ago. This screen scans and previews files. Cleanup is done in Windows Explorer.

Scans use the current user's permissions and can be canceled. Folder exclusions apply to the selected scope. Links, offline files, and unreadable locations are skipped, and differences involving unseen paths are marked as unverified. The numbers reflect logical file sizes by path. Compression, sparse files, and hard links can affect how much space those paths occupy on disk.

Snapshot history stays in `%LOCALAPPDATA%\AntiStorage`. It contains folder paths, file sizes, and timestamps. The app does not read file contents or connect to a server.

## Build from source

Use Windows with the [.NET Framework 4.8.1 Developer Pack](https://learn.microsoft.com/dotnet/framework/install/guide-for-developers) and MSBuild. The project uses WPF and has no third-party packages.

```powershell
msbuild .\AntiStorage.csproj /p:Configuration=Release
.\bin\Release\AntiStorage.exe
```

On Windows x64, you can also run the built-in MSBuild by its full path when MSBuild is unavailable on `PATH`.

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" .\AntiStorage.csproj /p:Configuration=Release
```
