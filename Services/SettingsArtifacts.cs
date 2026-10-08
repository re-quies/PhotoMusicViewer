using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace PhotoMusicViewer.Services
{
    internal static class SettingsArtifacts
    {
        internal static string NewArtifactPath(string target, string extension) =>
            target + ".pmv-" + Guid.NewGuid().ToString("N") + "." + extension;
        internal static bool IsOwnedName(string name, string settingsName)
        {
            if (string.Equals(name, settingsName, StringComparison.OrdinalIgnoreCase)) return true;
            const string guid = "[0-9a-f]{32}";
            return Regex.IsMatch(name, "^" + Regex.Escape(settingsName) +
                @"(?:\.(?:bak|tmp[0-9]*)|\." + guid + @"\.(?:bak|tmp)|\.pmv-" + guid + @"\.(?:bak|tmp))$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        internal static int Clean(string directory, string settingsName)
        {
            if (Path.GetFileName(settingsName) != settingsName) throw new ArgumentException("Invalid settings filename.");
            directory = Path.GetFullPath(directory);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory); }
            catch (DirectoryNotFoundException) { return 0; }
            catch (FileNotFoundException) { return 0; }
            if ((attributes & FileAttributes.Directory) == 0) throw new IOException("Settings path is not a directory.");
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to clean a linked settings directory.");
            int deleted = 0;
            var failures = new List<Exception>();
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsOwnedName(Path.GetFileName(path), settingsName)) continue;
                try
                {
                    // Не следуем ссылкам даже на файл с подходящим именем.
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Refusing to delete a linked settings artifact.");
                    File.Delete(path); deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add(ex); AppLog.Warn("SettingsArtifacts.Clean", ex);
                }
            }
            if (failures.Count > 0) throw new AggregateException("Some settings artifacts could not be removed.", failures);
            return deleted;
        }
    }
}
