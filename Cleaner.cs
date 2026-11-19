using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;

namespace AntiStorage
{
    internal sealed class CleanRule
    {
        internal string Name, Description, Browser;
        internal bool Recommended, Recoverable, TopOnly, AuditOnly;
        internal int AgeDays;
        internal IEnumerable<string> Roots;
        internal Func<string, bool> Match;
        public string Label { get { return Name; } }
        public string Hint { get { return Description; } }
        public bool Enabled { get; set; }
    }

    internal sealed class CleanFile
    {
        internal string Root, RelativePath, Rule, Browser, Hash;
        internal long Bytes;
        internal DateTime Modified, Created;
        internal bool Recoverable, AuditOnly;
        internal int Group;

        public string PathName { get { return Path.Combine(Root, RelativePath); } }
        public string Category { get { return Group == 0 ? Rule : "Group " + Group; } }
        public string Size { get { return Cleaner.FormatBytes(Bytes); } }
        public string LastChanged { get { return Modified.ToLocalTime().ToString("g"); } }
    }

    internal sealed class CleanScan
    {
        internal readonly List<CleanFile> Files = new List<CleanFile>();
        internal int Skipped;
    }

    [DataContract]
    internal sealed class CleanerPreferences
    {
        [DataMember] public List<string> Exclusions = new List<string>();
        [DataMember] public int IntervalDays;
        [DataMember] public DateTime LastReviewUtc;
        [DataMember] public List<string> IgnoredUpdates = new List<string>();
        [DataMember] public bool ScanOnOpen;

        internal static CleanerPreferences Load(string state)
        {
            try
            {
                var settings = Cleaner.Read<CleanerPreferences>(Path.Combine(state, "cleaner-settings.xml"));
                settings.Exclusions = settings.Exclusions ?? new List<string>();
                settings.IgnoredUpdates = settings.IgnoredUpdates ?? new List<string>();
                return settings;
            }
            catch (FileNotFoundException) { return new CleanerPreferences(); }
            catch (DirectoryNotFoundException) { return new CleanerPreferences(); }
        }

        internal void Save(string state)
        {
            Directory.CreateDirectory(state);
            var path = Path.Combine(state, "cleaner-settings.xml");
            var pending = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                Cleaner.Write(pending, this);
                if (File.Exists(path))
                    File.Replace(pending, path, null);
                else
                    File.Move(pending, path);
            }
            finally
            {
                if (File.Exists(pending))
                    File.Delete(pending);
            }
        }
    }

    internal static class Cleaner
    {
        private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        internal static List<CleanRule> Rules()
        {
            var rules = new List<CleanRule>
            {
                Rule("User temporary files", "Files created and modified at least two days ago.", 2, true, Path.Combine(Local, "Temp")),
                Rule("Windows temporary files", "Older system temporary files accessible to this account.", 7, false, Path.Combine(Win, "Temp")),
                Rule("Windows error reports", "Archived crash reports. Keep these if you are troubleshooting.", 7, false,
                    Path.Combine(Local, @"Microsoft\Windows\WER\ReportArchive"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportArchive")),
                Rule("DirectX shader cache", "Games and graphics apps can rebuild these files.", 2, true, Path.Combine(Local, "D3DSCache"), Path.Combine(Local, @"NVIDIA\DXCache")),
                Rule("Delivery Optimization cache", "Cached Microsoft update downloads.", 7, false,
                    Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
                    Path.Combine(Local, @"Microsoft\Windows\DeliveryOptimization\Cache")),
                Rule("Local crash dumps", "Crash evidence from desktop apps.", 7, false, Path.Combine(Local, "CrashDumps")),
                Rule("Thumbnail cache", "Windows recreates previews when needed.", 2, false, Path.Combine(Local, @"Microsoft\Windows\Explorer")),
                Rule("Recent file shortcuts", "Removes shortcuts in the Windows Recent folder.", 0, false, Path.Combine(Roaming, @"Microsoft\Windows\Recent")),
                Rule("Jump Lists", "Clears Windows Jump List data, including pinned entries in those lists.", 0, false,
                    Path.Combine(Roaming, @"Microsoft\Windows\Recent\AutomaticDestinations"),
                    Path.Combine(Roaming, @"Microsoft\Windows\Recent\CustomDestinations")),
                Rule("Downloads review", "Lists downloads older than 30 days. Move selected files to Recovery.", 30, false, Downloads()),
                Rule("AI app caches", "Rebuildable cache folders for known ChatGPT, Claude, Cursor and Codex installations.", 2, false,
                    AppCaches("ChatGPT", "Claude", "Cursor", "Codex").ToArray()),
                Rule("Developer and chat app caches", "Rebuildable Discord, Slack, VS Code and Teams caches.", 2, true,
                    AppCaches("discord", "Slack", "Code", "Microsoft Teams").ToArray()),
                Rule("Windows Store app temporary files", "Older files in per app TempState folders.", 7, false,
                    Directories(Path.Combine(Local, "Packages")).Select(path => Path.Combine(path, "TempState")).ToArray()),
                Rule("Local AI models", "Review downloaded Ollama blobs. Remove models through Ollama.", 0, false,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".ollama\models\blobs"))
            };
            rules[6].TopOnly = true;
            rules[6].Match = name => name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetExtension(name), ".db", StringComparison.OrdinalIgnoreCase);
            rules[7].TopOnly = true;
            rules[7].Match = name => string.Equals(Path.GetExtension(name), ".lnk", StringComparison.OrdinalIgnoreCase);
            rules[8].Match = name => name.EndsWith("Destinations-ms", StringComparison.OrdinalIgnoreCase);
            rules[9].Recoverable = true;
            rules[13].AuditOnly = true;
            foreach (var browser in Browsers())
            {
                var profiles = browser.Value;
                var chromium = browser.Key != "Firefox";
                var cacheRoots = chromium
                    ? profiles.SelectMany(path => new[] { Path.Combine(path, "Cache"), Path.Combine(path, "Code Cache"), Path.Combine(path, "GPUCache") })
                    : profiles.Select(path => Path.Combine(Local, "Mozilla", "Firefox", "Profiles", Path.GetFileName(path), "cache2"));
                var cache = Rule(browser.Key + " cache", "Cached web resources. Close the browser before cleaning.", 0, true, cacheRoots.ToArray());
                cache.Browser = browser.Key;
                rules.Add(cache);
                // Browser settings own synced history, cookies, saved credentials and site exceptions.
            }
            return rules;
        }

        internal static Dictionary<string, string[]> Browsers()
        {
            return new Dictionary<string, string[]>
            {
                { "Chrome", ChromiumProfiles(Path.Combine(Local, @"Google\Chrome\User Data")) },
                { "Edge", ChromiumProfiles(Path.Combine(Local, @"Microsoft\Edge\User Data")) },
                { "Brave", ChromiumProfiles(Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data")) },
                { "Firefox", Directories(Path.Combine(Roaming, @"Mozilla\Firefox\Profiles")) }
            };
        }

        internal static string BrowserProcess(string browser)
        {
            return browser == "Edge" ? "msedge" : browser.ToLowerInvariant();
        }

        private static string Downloads()
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath))
            {
                var configured = key == null ? null : key.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
                var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                return string.IsNullOrWhiteSpace(configured) ? fallback : Environment.ExpandEnvironmentVariables(configured);
            }
        }

        private static IEnumerable<string> AppCaches(params string[] apps)
        {
            return apps.SelectMany(app => new[] { "Cache", "Code Cache", "GPUCache" }.Select(dir => Path.Combine(Roaming, app, dir)));
        }

        private static string[] ChromiumProfiles(string root)
        {
            return Directories(root).Where(path => Path.GetFileName(path) == "Default" ||
                Path.GetFileName(path).StartsWith("Profile ", StringComparison.Ordinal)).ToArray();
        }

        private static string[] Directories(string root)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    ScanPaths.EnsureRegularDirectory(root);
                    return Directory.GetDirectories(root);
                }
            }
            catch (Exception error)
            {
                if (!ScanPaths.IsFileAccessError(error)) throw;
            }
            return new string[0];
        }

        private static CleanRule Rule(string name, string description, int days, bool recommended, params string[] paths)
        {
            return new CleanRule { Name = name, Description = description, AgeDays = days,
                Recommended = recommended, Enabled = recommended, Roots = paths.Where(IsFixedLocalPath) };
        }

        internal static bool IsFixedLocalPath(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && path.Length >= 3 && path[1] == ':' && path[2] == '\\' &&
                    new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Fixed;
            }
            catch (ArgumentException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static bool Excluded(string path, IEnumerable<string> exclusions)
        {
            return exclusions.Any(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase) ||
                ScanPaths.IsChildOf(path, item));
        }

        internal static CleanScan Scan(IEnumerable<CleanRule> rules, string[] exclusions, CancellationToken token)
        {
            var result = new CleanScan();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            foreach (var root in rule.Roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (Excluded(root, exclusions) || !Directory.Exists(root))
                    continue;
                try
                {
                    var files = Collect(root, rule.TopOnly, rule.AgeDays, exclusions, result, token);
                    foreach (var entry in files)
                    {
                        if ((rule.Match != null && !rule.Match(entry.RelativePath)) || !visited.Add(entry.PathName))
                            continue;
                        entry.Rule = rule.Name;
                        entry.Browser = rule.Browser;
                        entry.Recoverable = rule.Recoverable;
                        entry.AuditOnly = rule.AuditOnly || (rule.Recoverable && !CanMovePersonalFile(entry.PathName));
                        result.Files.Add(entry);
                    }
                }
                catch (Exception error)
                {
                    if (!ScanPaths.IsFileAccessError(error)) throw;
                    result.Skipped++;
                }
            }
            return result;
        }

        internal static List<CleanFile> Collect(string root, bool topOnly, int ageDays, string[] exclusions,
            CleanScan result, CancellationToken token)
        {
            ScanPaths.EnsureRegularDirectory(root);
            var cutoff = DateTime.UtcNow.AddDays(-ageDays);
            var files = new List<CleanFile>();
            var pending = new Stack<string>();
            var prefix = ScanPaths.DirectoryPrefix(root);
            pending.Push(root);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                if (Excluded(directory, exclusions))
                    continue;
                try
                {
                    ScanPaths.EnsureRegularDirectory(directory);
                    foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if (Excluded(item.FullName, exclusions))
                                continue;
                            if (ScanPaths.IsUnobserved(item.Attributes))
                            {
                                result.Skipped++;
                                continue;
                            }
                            var file = item as FileInfo;
                            if (file == null)
                            {
                                if (!topOnly) pending.Push(item.FullName);
                                continue;
                            }
                            if (file.CreationTimeUtc > cutoff || file.LastWriteTimeUtc > cutoff)
                                continue;
                            files.Add(new CleanFile { Root = root, RelativePath = file.FullName.Substring(prefix.Length),
                                Bytes = file.Length, Modified = file.LastWriteTimeUtc, Created = file.CreationTimeUtc });
                        }
                        catch (Exception error)
                        {
                            if (!ScanPaths.IsFileAccessError(error)) throw;
                            result.Skipped++;
                        }
                    }
                }
                catch (Exception error)
                {
                    if (!ScanPaths.IsFileAccessError(error)) throw;
                    result.Skipped++;
                }
            }
            return files;
        }

        internal static CleanScan Analyze(string root, string mode, long minimumBytes, int older, string[] exclusions,
            CancellationToken token)
        {
            var result = new CleanScan();
            var entries = Collect(root, false, older, exclusions, result, token).Where(file => file.Bytes >= minimumBytes);
            if (mode != "duplicates")
            {
                foreach (var file in entries)
                {
                    file.Rule = mode == "cloud" ? "Synced local file" : "Storage review";
                    file.Recoverable = true;
                    file.AuditOnly = mode == "cloud" || !CanMovePersonalFile(file.PathName);
                    result.Files.Add(file);
                }
                return result;
            }
            var group = 0;
            foreach (var sized in entries.Where(file => file.Bytes > 0).GroupBy(file => file.Bytes).Where(files => files.Skip(1).Any()))
            {
                var hashed = new List<CleanFile>();
                foreach (var file in sized)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (!CanMovePersonalFile(file.PathName))
                        {
                            result.Skipped++;
                            continue;
                        }
                        using (var handle = CheckedFile.Open(file, false, true))
                        {
                            file.Hash = handle.Hash(token);
                            hashed.Add(file);
                        }
                    }
                    catch (Exception error)
                    {
                        if (!ScanPaths.IsFileAccessError(error)) throw;
                        result.Skipped++;
                    }
                }
                foreach (var matches in hashed.GroupBy(file => file.Hash).Where(files => files.Skip(1).Any()))
                {
                    group++;
                    foreach (var file in matches)
                    {
                        file.Group = group;
                        file.Recoverable = true;
                        result.Files.Add(file);
                    }
                }
            }
            return result;
        }

        internal static bool CanMovePersonalFile(string path)
        {
            if (!IsFixedLocalPath(path)) return false;
            if (!string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(Local), StringComparison.OrdinalIgnoreCase))
                return false;
            var blocked = new[] { Win, Local, Roaming,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData"),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Path.GetPathRoot(path), "$Recycle.Bin"),
                Path.Combine(Path.GetPathRoot(path), "System Volume Information") };
            return !blocked.Where(item => !string.IsNullOrEmpty(item)).Any(item => ScanPaths.IsChildOf(path, item)) &&
                !path.Split(Path.DirectorySeparatorChar).Any(part => string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, ".ssh", StringComparison.OrdinalIgnoreCase));
        }

        internal static string FormatBytes(long bytes)
        {
            var units = new[] { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
            double value = bytes;
            var index = 0;
            while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
            return value.ToString(index == 0 ? "N0" : "N1", CultureInfo.CurrentCulture) + " " + units[index];
        }

        internal static bool BrowserRunning(string browser)
        {
            if (string.IsNullOrEmpty(browser)) return false;
            var processes = Process.GetProcessesByName(BrowserProcess(browser));
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        internal static T Read<T>(string path)
        {
            using (var stream = File.OpenRead(path))
                return (T)new DataContractSerializer(typeof(T)).ReadObject(stream);
        }

        internal static void Write<T>(string path, T value)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                new DataContractSerializer(typeof(T)).WriteObject(stream, value);
                stream.Flush(true);
            }
        }
    }
}
