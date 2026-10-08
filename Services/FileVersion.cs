using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace PhotoMusicViewer.Services
{
    internal readonly record struct FileVersion(long Length, long WriteTime, long CreationTime, uint Volume, ulong FileId)
    {
        internal static FileVersion Read(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(file, path);
        }
        internal static FileVersion Read(FileStream file, string path)
        {
            if (OperatingSystem.IsWindows() && GetFileInformationByHandle(file.SafeFileHandle, out var info))
                return new FileVersion(((long)info.SizeHigh << 32) | info.SizeLow,
                    ((long)info.WriteHigh << 32) | info.WriteLow, ((long)info.CreateHigh << 32) | info.CreateLow,
                    info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow);
            return new FileVersion(file.Length, File.GetLastWriteTimeUtc(path).Ticks, File.GetCreationTimeUtc(path).Ticks, 0, 0);
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Info
        {
            public uint Attributes, CreateLow, CreateHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
                Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Info info);
    }
}
