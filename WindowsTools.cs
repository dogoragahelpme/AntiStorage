using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AntiStorage
{
    internal sealed class ToolRow
    {
        public string Name { get; set; }
        public string Detail { get; set; }
        public string Source { get; set; }
        internal string Id, Revision, Command, Key;
        internal RegistryView View;
        internal RegistryHive Hive;
        internal RegistryValueKind Kind;
        internal int Pid;
        internal DateTime Started;
    }

    internal static class WindowsTools
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
        internal const string Startup = "Startup manager";
        internal const string Drivers = "Driver updates";
        internal const string Apps = "Uninstaller";
        internal const string Performance = "Performance";
        internal const string RegistryAudit = "Registry review";
        internal const string Software = "Software updates";
        internal const string SystemInfo = "System tools";

        internal static List<ToolRow> Read(string page, string filter, string state, CancellationToken token)
        {
            switch (page)
            {
                case Apps: return Installed(token);
                case Startup:
                    if (filter == "Services") return Query("Win32_Service", "DisplayName", "StartMode", "State", token);
                    if (filter == "Scheduled tasks") return ScheduledTasks(token);
                    if (filter == "Context menus") return ContextMenus(token);
                    if (filter == "Disabled by AntiStorage") return Backups(state, "startup").Select(record => record.Row).ToList();
                    return StartupRows(token);
                case Drivers:
                    if (filter == "Available drivers") return AvailableDrivers(token);
                    return Query("Win32_PnPSignedDriver", "DeviceName", "DriverVersion", "DriverProviderName", token);
                case Performance: return Processes(token);
                case RegistryAudit:
                    if (filter == "Backups") return Backups(state, "registry").Select(record => record.Row).ToList();
                    return BrokenPaths(token);
                case Software: return Updates(token);
                case SystemInfo:
                    return Query("Win32_OperatingSystem", "Caption", "Version", "OSArchitecture", token)
                        .Concat(Query("Win32_Processor", "Name", "NumberOfCores", "NumberOfLogicalProcessors", token))
                        .Concat(Query("Win32_VideoController", "Name", "DriverVersion", "AdapterCompatibility", token))
                        .Concat(Query("Win32_BaseBoard", "Product", "Manufacturer", "Version", token))
                        .Concat(DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                            .Select(drive => new ToolRow { Name = drive.Name, Detail = Cleaner.FormatBytes(drive.AvailableFreeSpace) + " available",
                                Source = Cleaner.FormatBytes(drive.TotalSize) + " capacity" })).ToList();
                default: return new List<ToolRow>();
            }
        }

        private static IEnumerable<Tuple<RegistryHive, RegistryView>> RegistryScopes()
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                yield return Tuple.Create(hive, RegistryView.Registry32);
                if (Environment.Is64BitOperatingSystem)
                    yield return Tuple.Create(hive, RegistryView.Registry64);
            }
        }

        private static List<ToolRow> Installed(CancellationToken token)
        {
            var rows = new List<ToolRow>();
            foreach (var scope in RegistryScopes())
            using (var hive = RegistryKey.OpenBaseKey(scope.Item1, scope.Item2))
            using (var parent = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (parent == null) continue;
                foreach (var id in parent.GetSubKeyNames())
                {
                    token.ThrowIfCancellationRequested();
                    using (var entry = parent.OpenSubKey(id))
                    {
                        if (entry == null || Convert.ToString(entry.GetValue("SystemComponent")) == "1") continue;
                        var name = entry.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var command = entry.GetValue("UninstallString") as string;
                        var msiProduct = Convert.ToString(entry.GetValue("WindowsInstaller")) == "1";
                        rows.Add(new ToolRow { Name = name, Detail = Convert.ToString(entry.GetValue("DisplayVersion")),
                            Source = Convert.ToString(entry.GetValue("Publisher")), Id = id, Command = command, Key = msiProduct ? "msi" : null,
                            Hive = scope.Item1, View = scope.Item2 });
                    }
                }
            }
            return rows.GroupBy(row => row.Name + "\0" + row.Detail + "\0" + row.Command, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).OrderBy(row => row.Name).ToList();
        }

        internal static void Uninstall(ToolRow row)
        {
            using (var parent = RegistryKey.OpenBaseKey(row.Hive, row.View))
            using (var entry = parent.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + row.Id))
            {
                if (entry == null || !string.Equals(entry.GetValue("DisplayName") as string, row.Name, StringComparison.Ordinal) ||
                    !string.Equals(entry.GetValue("UninstallString") as string, row.Command, StringComparison.Ordinal))
                    throw new IOException("The installed app entry changed. Refresh the list.");
                Guid product;
                if (row.Key == "msi" && Convert.ToString(entry.GetValue("WindowsInstaller")) == "1" && Guid.TryParseExact(row.Id, "B", out product))
                {
                    Launch(SystemExecutable("msiexec.exe"), "/x " + product.ToString("B"));
                    return;
                }
                // Use Windows' installed apps UI to invoke arbitrary vendor uninstall commands.
                Launch("ms-settings:appsfeatures");
            }
        }

        private static List<ToolRow> StartupRows(CancellationToken token)
        {
            var rows = new List<ToolRow>();
            foreach (var scope in RegistryScopes())
            using (var hive = RegistryKey.OpenBaseKey(scope.Item1, scope.Item2))
            foreach (var keyPath in new[] { RunKey, RunKey + "Once" })
            using (var key = hive.OpenSubKey(keyPath))
            {
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                {
                    token.ThrowIfCancellationRequested();
                    var command = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (command == null) continue;
                    rows.Add(new ToolRow { Name = name, Detail = command, Source = scope.Item1 + " / " + scope.Item2 +
                        (keyPath == RunKey ? " Run" : " RunOnce"), Id = name, Key = keyPath, Hive = scope.Item1,
                        View = scope.Item2, Command = command, Kind = key.GetValueKind(name) });
                }
            }
            rows = rows.GroupBy(row => row.Hive + "\0" + row.Key + "\0" + row.Id + "\0" + row.Command, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
            foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
            {
                var path = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
                foreach (var file in Directory.GetFiles(path).Where(file => !string.Equals(Path.GetFileName(file), "desktop.ini", StringComparison.OrdinalIgnoreCase)))
                    rows.Add(new ToolRow { Name = Path.GetFileName(file), Detail = file, Source = folder.ToString() });
            }
            return rows.OrderBy(row => row.Name).ToList();
        }

        private static List<ToolRow> ScheduledTasks(CancellationToken token)
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            try
            {
                service.Connect();
                var rows = new List<ToolRow>();
                TasksIn(service.GetFolder(@"\"), rows, token);
                return rows.OrderBy(row => row.Name).ToList();
            }
            finally { Marshal.ReleaseComObject(service); }
        }

        private static void TasksIn(dynamic folder, List<ToolRow> rows, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (dynamic task in folder.GetTasks(0))
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(new ToolRow { Name = task.Name, Detail = task.Path, Source = task.Enabled ? "Enabled" : "Disabled" });
                }
                foreach (dynamic child in folder.GetFolders(0))
                    TasksIn(child, rows, token);
            }
            catch (UnauthorizedAccessException) { }
            catch (COMException error) { if ((uint)error.ErrorCode != 0x80070005) throw; }
        }

        private static List<ToolRow> ContextMenus(CancellationToken token)
        {
            var result = new List<ToolRow>();
            foreach (var type in new[] { "*", "Directory", @"Directory\Background", "Drive", "Folder" })
            foreach (var location in new[] { @"\shell", @"\shellex\ContextMenuHandlers" })
            using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(type + location))
            {
                if (key == null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    token.ThrowIfCancellationRequested();
                    using (var entry = key.OpenSubKey(name))
                        result.Add(new ToolRow { Name = name, Detail = entry == null ? "" : Convert.ToString(entry.GetValue("")),
                            Source = type + location });
                }
            }
            return result;
        }

        private static List<ToolRow> Processes(CancellationToken token)
        {
            var rows = new List<ToolRow>();
            int session;
            int current;
            using (var self = Process.GetCurrentProcess()) { session = self.SessionId; current = self.Id; }
            foreach (var item in Process.GetProcesses())
            using (item)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (item.Id == current || item.SessionId != session) continue;
                    var window = item.MainWindowTitle;
                    rows.Add(new ToolRow { Name = item.ProcessName, Detail = Cleaner.FormatBytes(item.WorkingSet64),
                        Source = string.IsNullOrWhiteSpace(window) ? "Background process" : window,
                        Pid = item.Id, Started = item.StartTime.ToUniversalTime(), Key = item.MainWindowHandle == IntPtr.Zero ? null : "window",
                        Id = item.WorkingSet64.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return rows.OrderByDescending(row => long.Parse(row.Id, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        }

        internal static void CloseApplication(ToolRow row)
        {
            if (row.Key != "window") throw new IOException("Choose an application with a visible window.");
            using (var process = Process.GetProcessById(row.Pid))
            {
                if (process.StartTime.ToUniversalTime() != row.Started ||
                    !string.Equals(process.ProcessName, row.Name, StringComparison.Ordinal) || !process.CloseMainWindow())
                    throw new IOException("The application is no longer available to close.");
            }
        }

        private static List<ToolRow> Query(string type, string name, string detail, string source, CancellationToken token)
        {
            var rows = new List<ToolRow>();
            using (var query = new ManagementObjectSearcher("SELECT " + name + "," + detail + "," + source + " FROM " + type))
            using (var objects = query.Get())
            foreach (ManagementObject item in objects)
            using (item)
            {
                token.ThrowIfCancellationRequested();
                var label = Convert.ToString(item[name]);
                if (!string.IsNullOrWhiteSpace(label))
                    rows.Add(new ToolRow { Name = label, Detail = Convert.ToString(item[detail]), Source = Convert.ToString(item[source]) });
            }
            return rows.OrderBy(row => row.Name).ToList();
        }

        private static List<ToolRow> AvailableDrivers(CancellationToken token)
        {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", true));
            dynamic search = null;
            dynamic result = null;
            dynamic updates = null;
            var rows = new List<ToolRow>();
            try
            {
                search = session.CreateUpdateSearcher();
                result = search.Search("IsInstalled=0 and IsHidden=0 and Type='Driver'");
                updates = result.Updates;
                foreach (dynamic item in updates)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        rows.Add(new ToolRow { Name = item.Title, Detail = item.DriverClass, Source = item.DriverManufacturer });
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            finally
            {
                foreach (object item in new object[] { updates, result, search, session })
                    if (item != null) Marshal.ReleaseComObject(item);
            }
            return rows;
        }

        private static string WinGet()
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\winget.exe");
            if (!File.Exists(executable)) throw new IOException("Install or update App Installer from Microsoft Store to use WinGet.");
            return executable;
        }

        private static List<ToolRow> Updates(CancellationToken token)
        {
            var rows = new List<ToolRow>();
            using (var process = Process.Start(new ProcessStartInfo(WinGet(), "upgrade --source winget --disable-interactivity")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true
            }))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                var limit = Stopwatch.StartNew();
                while (!process.WaitForExit(200))
                {
                    if (!token.IsCancellationRequested && limit.Elapsed < TimeSpan.FromMinutes(2)) continue;
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    token.ThrowIfCancellationRequested();
                    throw new IOException("WinGet did not finish in two minutes.");
                }
                var text = output.GetAwaiter().GetResult();
                if (process.ExitCode == unchecked((int)0x8A15002B)) return rows;
                if (process.ExitCode != 0)
                    throw new IOException("WinGet could not check updates. " + (errors.GetAwaiter().GetResult() + " " + text).Trim());
                rows = ParseUpdates(text);
            }
            return rows;
        }

        private static List<ToolRow> ParseUpdates(string text)
        {
            var results = new List<ToolRow>();
            var table = false;
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length >= 8 && line.All(ch => ch == '-')) { table = true; continue; }
                if (!table) continue;
                var columns = Regex.Split(line, @"\s{2,}");
                if (columns.Length != 5 || !ValidPackage(columns[1]) ||
                    !string.Equals(columns[4], "winget", StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(new ToolRow { Name = columns[0], Id = columns[1], Revision = columns[3],
                    Detail = columns[2] + " → " + columns[3], Source = columns[1] });
            }
            return results;
        }

        private static bool ValidPackage(string id)
        {
            return !string.IsNullOrEmpty(id) && Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,179}\z");
        }

        internal static int Upgrade(ToolRow row)
        {
            if (!ValidPackage(row.Id) || row.Revision == null ||
                !Regex.IsMatch(row.Revision, @"\A[A-Za-z0-9][A-Za-z0-9._+-]{0,95}\z"))
                throw new IOException("WinGet returned an unsupported package or version.");
            using (var process = Process.Start(new ProcessStartInfo(WinGet(), "upgrade --id " + row.Id +
                " --exact --source winget --version " + row.Revision + " --interactive") { UseShellExecute = true }))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        private static List<ToolRow> BrokenPaths(CancellationToken token)
        {
            var rows = new List<ToolRow>();
            using (var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AppPaths))
            {
                if (parent == null) return rows;
                foreach (var name in parent.GetSubKeyNames())
                {
                    token.ThrowIfCancellationRequested();
                    using (var entry = parent.OpenSubKey(name))
                    {
                        if (entry == null || entry.ValueCount != 1 || entry.SubKeyCount != 0) continue;
                        var raw = entry.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                        if (!MissingExecutable(raw)) continue;
                        rows.Add(new ToolRow { Name = name, Detail = raw, Source = "Current user App Paths",
                            Id = name, Command = raw, Kind = entry.GetValueKind("") });
                    }
                }
            }
            return rows;
        }

        private static bool MissingExecutable(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            if (!Cleaner.IsFixedLocalPath(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                ScanPaths.EnsureRegularDirectory(Path.GetDirectoryName(path));
                File.GetAttributes(path);
                return false;
            }
            catch (FileNotFoundException) { return true; }
            catch (Exception error) { if (!ScanPaths.IsFileAccessError(error)) throw; return false; }
        }

        internal static void RemoveEntry(ToolRow row, string state, bool startup)
        {
            var kind = startup ? "startup" : "registry";
            var path = startup ? RunKey : AppPaths + @"\" + row.Id;
            if (startup && (row.Hive != RegistryHive.CurrentUser || row.Key != RunKey || string.IsNullOrEmpty(row.Id)))
                throw new IOException("Use Windows Startup Apps for this entry.");
            var value = startup ? row.Id : "";
            using (var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, startup ? row.View : RegistryView.Default))
            using (var key = hive.OpenSubKey(path, true))
            {
                if (key == null || !string.Equals(key.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                    row.Command, StringComparison.Ordinal) ||
                    (!startup && (key.ValueCount != 1 || key.SubKeyCount != 0 || !MissingExecutable(row.Command))))
                    throw new IOException("The entry changed. Refresh the list.");
                var entry = new RegistryBackup { Id = Guid.NewGuid().ToString("N"), Name = row.Id, Value = row.Command,
                    View = startup ? (int)row.View : (int)RegistryView.Default, Kind = (int)key.GetValueKind(value) };
                var folder = Path.Combine(state, kind);
                Directory.CreateDirectory(folder);
                ScanPaths.EnsureRegularDirectory(folder);
                var backup = Path.Combine(folder, entry.Id + ".xml");
                Cleaner.Write(backup, entry);
                try
                {
                    // Leave the key in place; a concurrent process might have added other values.
                    key.DeleteValue(value);
                }
                catch { File.Delete(backup); throw; }
            }
        }

        internal static void RestoreEntry(ToolRow row, string state, bool startup)
        {
            var backup = Backups(state, startup ? "startup" : "registry").FirstOrDefault(item => item.Id == row.Id);
            if (backup == null) throw new IOException("The backup was not found.");
            var value = startup ? backup.Name : "";
            var path = startup ? RunKey : AppPaths + @"\" + backup.Name;
            using (var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, (RegistryView)backup.View))
            using (var key = hive.CreateSubKey(path))
            {
                if (key.GetValueNames().Contains(value, StringComparer.OrdinalIgnoreCase))
                    throw new IOException("An entry with that name already exists.");
                key.SetValue(value, backup.Value, (RegistryValueKind)backup.Kind);
            }
            File.Delete(Path.Combine(state, startup ? "startup" : "registry", backup.Id + ".xml"));
        }

        private static IEnumerable<RegistryBackup> Backups(string state, string kind)
        {
            var folder = Path.Combine(state, kind);
            if (!Directory.Exists(folder)) yield break;
            ScanPaths.EnsureRegularDirectory(folder);
            foreach (var file in Directory.GetFiles(folder, "*.xml"))
            {
                if (ScanPaths.IsUnobserved(File.GetAttributes(file))) continue;
                Guid id;
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out id)) continue;
                var item = Cleaner.Read<RegistryBackup>(file);
                if (item.Id == id.ToString("N") && !string.IsNullOrEmpty(item.Name) && !item.Name.Contains(@"\") &&
                    Enum.IsDefined(typeof(RegistryView), item.View) &&
                    (item.Kind == (int)RegistryValueKind.String || item.Kind == (int)RegistryValueKind.ExpandString))
                    yield return item;
            }
        }

        internal static string SystemExecutable(string file)
        {
            return Path.Combine(Environment.SystemDirectory, file);
        }

        internal static void Launch(string path, string arguments = "")
        {
            using (Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true })) { }
        }
    }

    [System.Runtime.Serialization.DataContract]
    internal sealed class RegistryBackup
    {
        [System.Runtime.Serialization.DataMember] public string Id, Name, Value;
        [System.Runtime.Serialization.DataMember] public int View, Kind;
        internal ToolRow Row { get { return new ToolRow { Name = Name, Detail = Value, Source = "Backup", Id = Id }; } }
    }
}
