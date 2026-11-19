using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Security;
using System.Threading;

namespace AntiStorage
{
    internal static class ScanPaths
    {
        internal static string NormalizeDirectory(string path)
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath.Length == Path.GetPathRoot(fullPath).Length
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal static string DirectoryPrefix(string directory)
        {
            return directory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? directory
                : directory + Path.DirectorySeparatorChar;
        }

        internal static bool IsChildOf(string path, string directory)
        {
            return path.StartsWith(DirectoryPrefix(directory), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, directory, StringComparison.OrdinalIgnoreCase);
        }

        internal static void EnsureRegularDirectory(string path)
        {
            for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            {
                var attributes = File.GetAttributes(directory.FullName);
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new IOException("The selected location is not a folder.");
                if (IsUnobserved(attributes))
                    throw new IOException("Choose a folder that is not reached through a link or offline location.");
            }
        }

        internal static bool IsUnobserved(FileAttributes attributes)
        {
            return (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0;
        }

        internal static bool IsFileAccessError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is SecurityException;
        }
    }

    [DataContract]
    internal sealed class ScannedFile
    {
        [DataMember] public string RelativePath { get; set; }
        [DataMember] public long Bytes { get; set; }
        [DataMember] public DateTime ModifiedUtc { get; set; }
    }

    [DataContract]
    internal sealed class StorageSnapshot
    {
        [DataMember] public DateTime CapturedUtc { get; set; }
        [DataMember] public long TotalBytes { get; set; }
        [DataMember] public List<ScannedFile> Files { get; set; }
        [DataMember] public List<string> UnobservedPaths { get; set; }
    }

    internal static class StorageScan
    {
        internal static StorageSnapshot Capture(
            string root, IEnumerable<string> excludedFolders, DateTime? temporaryFileCutoffUtc,
            IProgress<int> progress, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ScanPaths.EnsureRegularDirectory(root);

            var rootPrefix = ScanPaths.DirectoryPrefix(root);
            var excluded = new HashSet<string>(excludedFolders, StringComparer.OrdinalIgnoreCase);
            var files = new List<ScannedFile>();
            var unobserved = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<DirectoryInfo>();
            var progressTimer = Stopwatch.StartNew();
            long totalBytes = 0;
            pending.Push(new DirectoryInfo(root));

            while (pending.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                try
                {
                    if (ScanPaths.IsUnobserved(File.GetAttributes(directory.FullName)))
                    {
                        unobserved.Add(RelativePath(directory.FullName, root, rootPrefix));
                        continue;
                    }

                    foreach (var entry in directory.EnumerateFileSystemInfos())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (excluded.Contains(entry.FullName))
                            continue;

                        var relativePath = entry.FullName.Substring(rootPrefix.Length);
                        try
                        {
                            if (ScanPaths.IsUnobserved(entry.Attributes))
                            {
                                unobserved.Add(relativePath);
                                continue;
                            }

                            var folder = entry as DirectoryInfo;
                            if (folder != null)
                            {
                                pending.Push(folder);
                                continue;
                            }

                            var file = (FileInfo)entry;
                            if (temporaryFileCutoffUtc.HasValue &&
                                (file.LastWriteTimeUtc > temporaryFileCutoffUtc.Value ||
                                 file.CreationTimeUtc > temporaryFileCutoffUtc.Value))
                                continue;

                            var bytes = file.Length;
                            files.Add(new ScannedFile
                            {
                                RelativePath = relativePath,
                                Bytes = bytes,
                                ModifiedUtc = file.LastWriteTimeUtc
                            });
                            totalBytes += bytes;
                        }
                        catch (Exception error)
                        {
                            if (!ScanPaths.IsFileAccessError(error))
                                throw;
                            unobserved.Add(relativePath);
                        }

                        if (progress != null && progressTimer.ElapsedMilliseconds >= 250)
                        {
                            progress.Report(files.Count);
                            progressTimer.Restart();
                        }
                    }
                }
                catch (Exception error)
                {
                    if (!ScanPaths.IsFileAccessError(error))
                        throw;
                    if (string.Equals(directory.FullName, root, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("AntiStorage could not read the selected folder in full.", error);
                    unobserved.Add(RelativePath(directory.FullName, root, rootPrefix));
                }
            }

            cancellation.ThrowIfCancellationRequested();
            return new StorageSnapshot
            {
                CapturedUtc = DateTime.UtcNow,
                Files = files,
                TotalBytes = totalBytes,
                UnobservedPaths = new List<string>(unobserved)
            };
        }

        private static string RelativePath(string directory, string root, string rootPrefix)
        {
            return string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : directory.Substring(rootPrefix.Length);
        }
    }
}
