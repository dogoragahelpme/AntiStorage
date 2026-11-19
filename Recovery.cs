using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace AntiStorage
{
    [DataContract]
    internal sealed class RecoveryItem
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Original { get; set; }
        [DataMember] public long Bytes { get; set; }
        [DataMember] public DateTime Modified { get; set; }
        [DataMember] public DateTime Created { get; set; }
        [DataMember] public DateTime MovedUtc { get; set; }
        public string Size { get { return Cleaner.FormatBytes(Bytes); } }
        public string Moved { get { return MovedUtc.ToLocalTime().ToString("g"); } }
    }

    internal sealed class RecoveryStore
    {
        internal string Root { get; private set; }

        internal RecoveryStore(string state) { Root = Path.Combine(state, "Recovery"); }

        private string PathFor(string id, string extension)
        {
            Guid parsed;
            if (!Guid.TryParseExact(id, "N", out parsed)) throw new IOException("Invalid recovery record.");
            return Path.Combine(Root, id + extension);
        }

        internal void Save(CleanFile file, CheckedFile handle)
        {
            if (!Cleaner.CanMovePersonalFile(file.PathName)) throw new IOException("This location is protected.");
            if (!string.Equals(Path.GetPathRoot(file.PathName), Path.GetPathRoot(Root), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Recovery can move files on the Windows profile drive only.");
            ScanPaths.EnsureRegularDirectory(Path.GetDirectoryName(Path.GetDirectoryName(Root)));
            Directory.CreateDirectory(Root);
            ScanPaths.EnsureRegularDirectory(Root);
            var item = new RecoveryItem { Id = Guid.NewGuid().ToString("N"), Original = file.PathName,
                Bytes = file.Bytes, Modified = file.Modified, Created = file.Created, MovedUtc = DateTime.UtcNow };
            var manifest = PathFor(item.Id, ".xml");
            Cleaner.Write(manifest, item);
            try { handle.MoveTo(PathFor(item.Id, ".data")); }
            catch { File.Delete(manifest); throw; }
        }

        internal List<RecoveryItem> List()
        {
            var result = new List<RecoveryItem>();
            if (!Directory.Exists(Root)) return result;
            ScanPaths.EnsureRegularDirectory(Root);
            foreach (var manifest in Directory.EnumerateFiles(Root, "*.xml"))
            {
                var id = Path.GetFileNameWithoutExtension(manifest);
                Guid parsed;
                if (!Guid.TryParseExact(id, "N", out parsed) || !File.Exists(PathFor(id, ".data")) ||
                    ScanPaths.IsUnobserved(File.GetAttributes(manifest)))
                    continue;
                var item = Cleaner.Read<RecoveryItem>(manifest);
                if (item.Id == id) result.Add(item);
            }
            return result.OrderByDescending(item => item.MovedUtc).ToList();
        }

        internal void Resolve(RecoveryItem item, bool restore)
        {
            var data = PathFor(item.Id, ".data");
            var file = new CleanFile { Root = Root, RelativePath = Path.GetFileName(data), Bytes = item.Bytes,
                Modified = item.Modified, Created = item.Created };
            using (var handle = CheckedFile.Open(file, true, false))
            {
                if (restore)
                {
                    if (!Cleaner.CanMovePersonalFile(item.Original) || !string.Equals(item.Original, Path.GetFullPath(item.Original), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Original location is protected or unavailable.");
                    handle.MoveTo(item.Original);
                }
                else handle.Delete();
            }
            File.Delete(PathFor(item.Id, ".xml"));
        }
    }
}
