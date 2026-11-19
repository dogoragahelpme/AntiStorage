using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AntiStorage
{
    [DataContract]
    internal sealed class SnapshotHistory
    {
        [DataMember] public StorageSnapshot Previous { get; set; }
        [DataMember] public StorageSnapshot Latest { get; set; }
    }

    [DataContract]
    internal sealed class FolderScope
    {
        [DataMember] public string Root { get; set; }
        [DataMember] public List<string> Exclusions { get; set; }
    }

    internal sealed class SnapshotStore
    {
        internal string StateDirectory { get; private set; }

        internal SnapshotStore(string stateDirectory)
        {
            StateDirectory = ScanPaths.NormalizeDirectory(stateDirectory);
        }

        internal FolderScope ReadLastScope()
        {
            return Read<FolderScope>(Path.Combine(StateDirectory, "last-folder.json"));
        }

        internal SnapshotHistory ReadHistory(FolderScope scope)
        {
            return Read<SnapshotHistory>(GetHistoryPath(scope)) ?? new SnapshotHistory();
        }

        internal SnapshotHistory Record(FolderScope scope, StorageSnapshot snapshot, CancellationToken cancellation)
        {
            var path = GetHistoryPath(scope);
            using (var mutex = new Mutex(false, @"Local\AntiStorage-" + Hash(StateDirectory.ToUpperInvariant())))
            {
                try
                {
                    if (WaitHandle.WaitAny(new[] { cancellation.WaitHandle, mutex }) == 0)
                        throw new OperationCanceledException(cancellation);
                }
                catch (AbandonedMutexException)
                {
                    // An interrupted writer can leave a temporary file, and each published JSON file is atomic.
                }

                try
                {
                    var existing = ReadHistory(scope);
                    var newest = new[] { snapshot, existing.Latest, existing.Previous }
                        .Where(item => item != null)
                        .OrderByDescending(item => item.CapturedUtc)
                        .Take(2).ToArray();
                    var history = new SnapshotHistory
                    {
                        Latest = newest[0],
                        Previous = newest.Length == 2 ? newest[1] : null
                    };
                    cancellation.ThrowIfCancellationRequested();
                    WriteAtomic(Path.Combine(StateDirectory, "last-folder.json"), scope);
                    cancellation.ThrowIfCancellationRequested();
                    WriteAtomic(path, history, cancellation);
                    return history;
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private string GetHistoryPath(FolderScope scope)
        {
            var paths = new[] { scope.Root }.Concat(scope.Exclusions.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            var key = string.Join("\n", paths).ToUpperInvariant();
            return Path.Combine(StateDirectory, "snapshots", Hash(key) + ".json");
        }

        private static string Hash(string text)
        {
            using (var sha256 = SHA256.Create())
                return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

        private static T Read<T>(string path) where T : class
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(file);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        private static void WriteAtomic<T>(string path, T value, CancellationToken cancellation = default(CancellationToken))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(T)).WriteObject(file, value);
                    file.Flush(true);
                }
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    internal sealed class FileChange
    {
        internal string RelativePath { get; set; }
        internal long? Before { get; set; }
        internal long? After { get; set; }
        internal long Delta { get { return After.GetValueOrDefault() - Before.GetValueOrDefault(); } }
    }

    internal sealed class SnapshotComparison
    {
        internal long ObservedDelta { get; private set; }
        internal List<FileChange> Changes { get; private set; }
        internal int UnknownCount { get; private set; }

        internal static SnapshotComparison Create(StorageSnapshot previous, StorageSnapshot latest)
        {
            var result = new SnapshotComparison { Changes = new List<FileChange>() };
            var olderFiles = previous.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
            var olderUnknown = new HashSet<string>(previous.UnobservedPaths, StringComparer.OrdinalIgnoreCase);
            var newerUnknown = new HashSet<string>(latest.UnobservedPaths, StringComparer.OrdinalIgnoreCase);

            foreach (var file in latest.Files)
            {
                ScannedFile oldFile;
                if (olderFiles.TryGetValue(file.RelativePath, out oldFile))
                {
                    olderFiles.Remove(file.RelativePath);
                    if (oldFile.Bytes != file.Bytes)
                        result.AddChange(file.RelativePath, oldFile.Bytes, file.Bytes);
                }
                else if (WasUnobserved(file.RelativePath, olderUnknown))
                    result.UnknownCount++;
                else
                    result.AddChange(file.RelativePath, null, file.Bytes);
            }

            foreach (var file in olderFiles.Values)
            {
                if (WasUnobserved(file.RelativePath, newerUnknown))
                    result.UnknownCount++;
                else
                    result.AddChange(file.RelativePath, file.Bytes, null);
            }

            return result;
        }

        private static bool WasUnobserved(string path, HashSet<string> unobserved)
        {
            if (unobserved.Contains(string.Empty))
                return true;
            while (path != null && path.Length != 0)
            {
                if (unobserved.Contains(path))
                    return true;
                path = Path.GetDirectoryName(path);
            }
            return false;
        }

        private void AddChange(string path, long? before, long? after)
        {
            var change = new FileChange { RelativePath = path, Before = before, After = after };
            Changes.Add(change);
            ObservedDelta += change.Delta;
        }
    }
}
