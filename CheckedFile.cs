using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AntiStorage
{
    // The open handle identifies the item throughout validation and mutation.
    internal sealed class CheckedFile : IDisposable
    {
        private readonly SafeFileHandle handle;

        private CheckedFile(SafeFileHandle handle) { this.handle = handle; }

        internal static CheckedFile Open(CleanFile file, bool modify, bool readContent)
        {
            var path = file.PathName;
            if (!Cleaner.IsFixedLocalPath(path) || !ScanPaths.IsChildOf(path, file.Root) ||
                !string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The file is outside the scan location.");
            ScanPaths.EnsureRegularDirectory(Path.GetDirectoryName(path));
            const uint readAttributes = 0x80, deleteAccess = 0x10000, readData = 0x80000000;
            const uint openReparsePoint = 0x00200000;
            var access = readAttributes | (modify ? deleteAccess : 0) | (readContent ? readData : 0);
            var native = CreateFile(path, access, 1, IntPtr.Zero, 3, openReparsePoint, IntPtr.Zero);
            try
            {
                if (native.IsInvalid) throw Error();
                var actual = ResolvedPath(native);
                FileInformation information;
                if (!GetFileInformationByHandle(native, out information)) throw Error();
                var attributes = (FileAttributes)information.Attributes;
                var length = ((long)information.SizeHigh << 32) | information.SizeLow;
                var modified = DateTime.FromFileTimeUtc(((long)information.WriteHigh << 32) | information.WriteLow);
                var created = DateTime.FromFileTimeUtc(((long)information.CreationHigh << 32) | information.CreationLow);
                if (!string.Equals(actual, path, StringComparison.OrdinalIgnoreCase) || ScanPaths.IsUnobserved(attributes) ||
                    (attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0 ||
                    information.LinkCount != 1 || length != file.Bytes || modified != file.Modified ||
                    (file.Created != default(DateTime) && created != file.Created))
                    throw new IOException("The file changed, is linked, or is unavailable. Scan again.");
                return new CheckedFile(native);
            }
            catch { native.Dispose(); throw; }
        }

        internal string Hash(CancellationToken token)
        {
            var borrowed = new SafeFileHandle(handle.DangerousGetHandle(), false);
            using (var stream = new FileStream(borrowed, FileAccess.Read))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[128 * 1024];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return Convert.ToBase64String(sha.Hash);
            }
        }

        internal void Delete()
        {
            var disposition = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(disposition, 1);
                if (!SetFileInformationByHandle(handle, 4, disposition, 1)) throw Error();
            }
            finally { Marshal.FreeHGlobal(disposition); }
        }

        internal void MoveTo(string destination)
        {
            using (OpenDestinationDirectory(Path.GetDirectoryName(destination)))
            {
                var name = Encoding.Unicode.GetBytes(destination);
                var rootOffset = IntPtr.Size;
                var bytesOffset = rootOffset + IntPtr.Size;
                var nameOffset = bytesOffset + sizeof(int);
                var size = Math.Max(nameOffset + name.Length + sizeof(char), IntPtr.Size == 8 ? 24 : 16);
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.Copy(new byte[size], 0, buffer, size);
                    Marshal.WriteInt32(buffer, bytesOffset, name.Length);
                    Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
                    if (!SetFileInformationByHandle(handle, 3, buffer, (uint)size)) throw Error();
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }

        private static SafeFileHandle OpenDestinationDirectory(string directory)
        {
            ScanPaths.EnsureRegularDirectory(directory);
            var parent = CreateFile(directory, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            try
            {
                if (parent.IsInvalid) throw Error();
                var actual = ResolvedPath(parent);
                if (!string.Equals(actual, directory, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The destination folder changed.");
                return parent;
            }
            catch { parent.Dispose(); throw; }
        }

        private static string ResolvedPath(SafeFileHandle file)
        {
            var buffer = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) throw Error();
            var actual = buffer.ToString();
            if (!actual.StartsWith(@"\\?\", StringComparison.Ordinal)) throw new IOException("The path could not be verified.");
            return actual.Substring(4);
        }

        private static IOException Error()
        {
            return new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        public void Dispose() { handle.Dispose(); }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint Volume, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr data, uint size);
    }
}
