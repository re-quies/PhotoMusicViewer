using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PhotoMusicViewer.Services
{
    public sealed class BatchRenamePlanItem
    {
        public string OldPath { get; init; } = "";
        public string NewPath { get; init; } = "";
    }

    public sealed class BatchRenameResult
    {
        public int RenamedCount { get; set; }
        public string? ErrorMessage { get; set; }
        public Dictionary<string, string> OldToNew { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Массовое переименование файлов в порядковые номера (001.jpg, 002.png, ...).
    ///
    /// Безопасность — главный приоритет:
    /// 1. Сначала строится полный план (старое имя -> новое имя) без единого касания диска.
    /// 2. Номера, уже занятые существующими числовыми именами, пропускаются.
    /// 3. Целевые имена уникальны по построению + проверяются на существование
    ///    дважды: при построении плана и непосредственно перед каждым переименованием.
    /// 4. File.Move никогда не перезаписывает существующий файл (бросает IOException) —
    ///    даже при гонке с другим процессом потеря данных невозможна.
    /// 5. При любой ошибке процесс останавливается: уже переименованные файлы остаются
    ///    под новыми именами, остальные — под старыми. Ничего не теряется и не затирается.
    /// </summary>
    public static class BatchRenameService
    {
        public static List<BatchRenamePlanItem> BuildPlan(
            string folder,
            bool recursive,
            long startNumber,
            int zeroPadding,
            IReadOnlyCollection<string> extensions)
        {
            // Все файлы в области действия (любых расширений) — нужны для учёта занятых номеров
            var allFiles = new List<string>();
            CollectFiles(folder, recursive, allFiles);

            // Кандидаты: только поддерживаемые фото/GIF с ещё не числовыми именами
            var eligible = allFiles
                .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Where(f => !IsNumericName(Path.GetFileNameWithoutExtension(f)))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Занятые номера — числовые имена любых существующих файлов в области действия
            var usedNumbers = new HashSet<long>();
            foreach (var f in allFiles)
            {
                var n = Path.GetFileNameWithoutExtension(f);
                if (IsNumericName(n) && long.TryParse(n, out var num))
                    usedNumbers.Add(num);
            }

            var plan = new List<BatchRenamePlanItem>();
            var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long current = startNumber;

            foreach (var oldPath in eligible)
            {
                var dir = Path.GetDirectoryName(oldPath)!;
                var ext = Path.GetExtension(oldPath);

                string newPath;
                while (true)
                {
                    while (usedNumbers.Contains(current)) current++;

                    var newName = current.ToString().PadLeft(zeroPadding, '0') + ext;
                    newPath = Path.Combine(dir, newName);

                    // Страховка: имя свободно и на диске, и в уже запланированных переименованиях
                    if (!File.Exists(newPath) && !plannedTargets.Contains(newPath)) break;
                    current++;
                }

                usedNumbers.Add(current);
                plannedTargets.Add(newPath);
                plan.Add(new BatchRenamePlanItem { OldPath = oldPath, NewPath = newPath });
                current++;
            }

            return plan;
        }

        public static BatchRenameResult Execute(List<BatchRenamePlanItem> plan)
        {
            var result = new BatchRenameResult();

            foreach (var item in plan)
            {
                try
                {
                    if (!File.Exists(item.OldPath))
                        continue; // файл исчез между планированием и выполнением — просто пропускаем

                    if (File.Exists(item.NewPath))
                    {
                        // Кто-то успел создать файл с целевым именем — останавливаемся, ничего не трогая
                        result.ErrorMessage =
                            $"Stopped: target name already exists ({Path.GetFileName(item.NewPath)}). " +
                            "No files were overwritten or lost.";
                        break;
                    }

                    // File.Move никогда не перезаписывает существующий файл — в худшем случае исключение
                    File.Move(item.OldPath, item.NewPath);
                    result.OldToNew[item.OldPath] = item.NewPath;
                    result.RenamedCount++;
                }
                catch (Exception ex)
                {
                    result.ErrorMessage =
                        $"Stopped at \"{Path.GetFileName(item.OldPath)}\": {ex.Message} " +
                        "Files already renamed keep their new names; nothing was overwritten or lost.";
                    break;
                }
            }

            return result;
        }

        private static void CollectFiles(string folder, bool recursive, List<string> results)
        {
            try
            {
                results.AddRange(Directory.EnumerateFiles(folder));
            }
            catch
            {
                // нет доступа к папке — просто пропускаем её
            }

            if (!recursive) return;

            List<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(folder).ToList();
            }
            catch
            {
                return;
            }

            foreach (var sub in subdirs)
                CollectFiles(sub, true, results);
        }

        private static bool IsNumericName(string name) =>
            name.Length > 0 && name.All(char.IsDigit);
    }
}
