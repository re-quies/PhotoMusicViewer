using System;
using System.IO;

namespace PhotoMusicViewer.Services
{
    public static class PrivacyCleanupService
    {
        /// <summary>
        /// Удаляет запись о только что открытом файле/папке из системного списка
        /// Windows "Недавние файлы" (%APPDATA%\Microsoft\Windows\Recent), куда
        /// стандартные диалоги выбора файла/папки добавляют её автоматически.
        /// </summary>
        public static void RemoveFromRecentItems(string openedPath)
        {
            try
            {
                string recentFolder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                if (!Directory.Exists(recentFolder)) return;

                string targetName = Path.GetFileName(openedPath.TrimEnd(Path.DirectorySeparatorChar));

                foreach (var lnk in Directory.EnumerateFiles(recentFolder, "*.lnk"))
                {
                    string lnkTargetName = Path.GetFileNameWithoutExtension(lnk);
                    if (string.Equals(lnkTargetName, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(lnk);
                    }
                }
            }
            catch
            {
                // Не критично - если не получилось убрать запись, просто оставляем как есть, без окон
            }
        }
    }
}