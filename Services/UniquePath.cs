using System;
using System.IO;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Подбор свободного имени файла в стиле проводника: «name.jpg» → «name (1).jpg».
    /// Отдельный помощник, потому что этот код успел разойтись копиями по нескольким
    /// местам приложения.
    /// </summary>
    internal static class UniquePath
    {
        public static string GetAvailablePath(string path)
        {
            if (!File.Exists(path)) return path;

            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                throw new ArgumentException("Path must include a directory.", nameof(path));

            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);

            for (int counter = 1; counter < int.MaxValue; counter++)
            {
                var candidate = Path.Combine(dir, $"{name} ({counter}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }

            throw new IOException($"No free file name next to {path}.");
        }
    }
}
