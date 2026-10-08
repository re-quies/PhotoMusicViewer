using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhotoMusicViewer.Services
{
    /// <summary>Fail-closed ancestry checks. Windows leases pin each component against
    /// rename/delete and resolve its actual name. Keep the lease until mutation is finished.</summary>
    internal sealed class DirectoryPathGuard : IDisposable
    {
        private readonly List<(string Path, string Actual, SafeFileHandle? Handle)> _components = new();
        private bool _disposed;
        internal string ActualDirectory { get; private set; } = "";

        internal static string Normalize(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
                else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) path = path[4..];
                if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) || path.StartsWith("GLOBALROOT", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Device paths are not allowed for file operations.");
            }
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }

        internal static DirectoryPathGuard Acquire(string directory)
        {
            var lease = new DirectoryPathGuard();
            try
            {
                var parents = new List<string>();
                string full = Normalize(directory);
                for (string? current = full; current != null; current = Path.GetDirectoryName(current))
                {
                    parents.Add(current);
                    string root = Normalize(Path.GetPathRoot(current)!);
                    if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
                }
                parents.Reverse();
                foreach (string current in parents)
                {
                    RequireDirectory(current);
                    SafeFileHandle? handle = null;
                    try
                    {
                        string actual = current;
                        if (OperatingSystem.IsWindows())
                        {
                            // Read/write sharing allows normal operations on children; no
                            // FILE_SHARE_DELETE means ancestors cannot be renamed/replaced.
                            handle = OpenDirectory(current, share: 1 | 2, openReparsePoint: true);
                            RequireHandleDirectory(handle);
                            actual = FinalPath(handle);
                        }
                        lease._components.Add((current, actual, handle));
                        handle = null;
                    }
                    finally { handle?.Dispose(); }
                }
                lease.ActualDirectory = lease._components[^1].Actual;
                if (OperatingSystem.IsWindows() && !string.Equals(full, lease.ActualDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    // A mapped/SUBST/short-name path may omit physical ancestors.
                    // Pin those too, not just the textual drive/UNC components.
                    using var physical = Acquire(lease.ActualDirectory);
                    lease._components.AddRange(physical._components);
                    physical._components.Clear(); // ownership transferred to this lease
                }
                lease.Validate();
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        internal string FilePath(string path) => Path.Combine(ActualDirectory, Path.GetFileName(Path.GetFullPath(path)));

        internal static DirectoryPathGuard AcquireOrCreate(string directory)
        {
            string full = Normalize(directory);
            var missing = new List<string>();
            string? existing = full;
            while (existing != null)
            {
                try { File.GetAttributes(existing); break; }
                catch (FileNotFoundException) { missing.Add(existing); }
                catch (DirectoryNotFoundException) { missing.Add(existing); }
                existing = Path.GetDirectoryName(existing);
            }
            if (existing == null) throw new IOException("No verified directory ancestor is available.");
            var guards = new List<DirectoryPathGuard>();
            try
            {
                guards.Add(Acquire(existing));
                missing.Reverse();
                foreach (string child in missing)
                {
                    guards[^1].Validate();
                    Directory.CreateDirectory(child);
                    guards.Add(Acquire(child));
                }
                return Acquire(full);
            }
            finally { for (int i = guards.Count - 1; i >= 0; i--) guards[i].Dispose(); }
        }

        internal void Validate()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var component in _components)
            {
                RequireDirectory(component.Path);
                if (component.Handle != null)
                {
                    RequireHandleDirectory(component.Handle);
                    if (!string.Equals(FinalPath(component.Handle), component.Actual, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(ResolveActual(component.Path), component.Actual, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Directory location changed; no file operation is allowed.");
                }
            }
        }

        // Read-only resolution for known protected/temp directories. The operational
        // path itself must always go through Acquire, which rejects all reparse parents.
        internal static string ResolveActual(string directory)
        {
            if (!OperatingSystem.IsWindows()) return Normalize(directory);
            using var handle = OpenDirectory(Normalize(directory), share: 1 | 2 | 4, openReparsePoint: false);
            return FinalPath(handle);
        }

        internal static void RequireRegularFile(string path, bool allowMissing = false)
        {
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("Linked files and directories are not allowed in this operation.");
            }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }

        private static void RequireDirectory(string path)
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Directory) == 0 || (attrs & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A parent directory is linked, missing, or not a directory; operation refused.");
        }

        private static SafeFileHandle OpenDirectory(string path, uint share, bool openReparsePoint)
        {
            const uint ReadAttributes = 0x80, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
            var handle = CreateFileW(path, ReadAttributes, share, IntPtr.Zero, 3,
                BackupSemantics | (openReparsePoint ? OpenReparsePoint : 0), IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error(); handle.Dispose();
                throw new IOException("Cannot safely open a directory.", new Win32Exception(error));
            }
            return handle;
        }
        private static void RequireHandleDirectory(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw new IOException("Cannot verify directory attributes.", new Win32Exception(Marshal.GetLastWin32Error()));
            if ((info.Attributes & (uint)FileAttributes.Directory) == 0 || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new IOException("A directory handle refers to a reparse point; operation refused.");
        }
        private static string FinalPath(SafeFileHandle handle)
        {
            var buffer = new StringBuilder(512);
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) throw new IOException("Cannot resolve actual directory path.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot resolve actual directory path.");
            }
            return Normalize(buffer.ToString());
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            for (int i = _components.Count - 1; i >= 0; i--) _components[i].Handle?.Dispose();
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Info
        {
            public uint Attributes, CreateLow, CreateHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
                Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint count, uint flags);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    }
}
