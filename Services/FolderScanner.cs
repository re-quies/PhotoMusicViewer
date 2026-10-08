using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PhotoMusicViewer.Services
{
    /// <summary>Файл из листинга папки с метаданными, полученными тем же системным вызовом.</summary>
    internal readonly record struct FolderEntry(string Path, DateTime LastWriteUtc, long Length);

    /// <summary>
    /// Листинг папки для фонового потока. Размер и время изменения берутся из данных
    /// самого перечисления (FindFirstFile/FindNextFile на Windows), а не отдельным
    /// запросом на каждый файл — на сетевой папке это разница между одним обходом
    /// и тысячами обращений к серверу. Недоступные элементы пропускаются, а не
    /// обрывают весь список. Отмена проверяется по ходу обхода.
    /// </summary>
    internal static class FolderScanner
    {
        // Как Directory.EnumerateFiles(path): скрытые и системные файлы не отбрасываем.
        private static readonly EnumerationOptions Options = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };

        internal static List<FolderEntry> ScanFiles(string folder, Func<string, bool> include, CancellationToken token = default)
        {
            var result = new List<FolderEntry>();
            int n = 0;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", Options))
            {
                if ((++n & 255) == 0) token.ThrowIfCancellationRequested();
                if (!include(file.FullName)) continue;
                DateTime written; long length;
                try { written = file.LastWriteTimeUtc; length = file.Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    written = DateTime.MinValue; length = 0;
                }
                result.Add(new FolderEntry(file.FullName, written, length));
            }
            token.ThrowIfCancellationRequested();
            return result;
        }

        internal static List<string> ScanDirectories(string folder, CancellationToken token = default)
        {
            var result = new List<string>();
            int n = 0;
            foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories("*", Options))
            {
                if ((++n & 255) == 0) token.ThrowIfCancellationRequested();
                result.Add(dir.FullName);
            }
            token.ThrowIfCancellationRequested();
            result.Sort(NaturalStringComparer.FileName); // «Поездка 2» раньше «Поездка 10»
            return result;
        }

        internal static Dictionary<string, FolderEntry> ToLookup(IEnumerable<FolderEntry> entries)
        {
            var lookup = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries) lookup[entry.Path] = entry;
            return lookup;
        }

        /// <summary>
        /// Новый список папки с сохранением текущего файла: если его нет в листинге
        /// (удалён снаружи, скрыт, ещё не виден на сетевом ресурсе), он остаётся в списке,
        /// чтобы просмотр не сломался — уход с него уберёт его обычным путём.
        /// Возвращает индекс текущего файла (или -1, если текущего нет).
        /// </summary>
        internal static int MergeKeepingCurrent(List<string> files, string? current, Func<List<string>, List<string>> sort, out List<string> merged)
        {
            merged = files;
            if (current == null) return -1;
            int index = merged.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) return index;
            merged = sort(merged.Append(current).ToList());
            return merged.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
        }
    }
}
